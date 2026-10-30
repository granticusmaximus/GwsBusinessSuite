using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// One 511 site on the shared 511 platform (Alaska, seven Canadian provinces and territories, and
// AZ/WI/NV/UT/LA/CT/PA, Ontario and Alberta). The platform's developer API needs a registered
// key; Grant chose key-less (29-10-26), so this reads what the public map itself loads:
// GET /List/GetData/Cameras?query={...} (with X-Requested-With) pages through every camera site
// 100 at a time, each with a WKT point and one or more images at /map/Cctv/{id} - plain JPEGs
// refreshed in place, so Snapshot feeds. Verified live on all 16 sites 29-10-26.
public sealed partial class Platform511CameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<Platform511CameraProvider> logger,
    string host,
    string sourceName,
    BoundingBox coverage,
    TimeProvider? time = null) : ICameraFeedProvider
{
    private const int PageSize = 100;
    // Utah, the biggest, is 21 pages; this stops a site that misreports its total from looping.
    private const int MaxPages = 60;
    private const int PagesAtOnce = 4;
    // After this a site's list is refreshed in the background while the old one keeps serving;
    // a list nobody asks for in KeepFor is dropped.
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(6);
    // One download per site (and cache) at a time, shared by every view that asks while it runs (a
    // pan used to start another 20 requests whenever the cache had just expired).
    private static readonly ConcurrentDictionary<(IMemoryCache Cache, string Host), Task<IReadOnlyList<CameraFeed>>> Downloads = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Snapshot(DateTimeOffset At, IReadOnlyList<CameraFeed> Cameras);

    public string SourceName => sourceName;

    public BoundingBox? Coverage => coverage;

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"camera-intel:511-platform:{host}";
        IReadOnlyList<CameraFeed> allCameras;
        if (cache.TryGetValue(cacheKey, out Snapshot? snapshot) && snapshot is not null)
        {
            // An old list serves at once; a fresh one is fetched behind it for the next view.
            if (_time.GetUtcNow() - snapshot.At > FreshFor)
                _ = DownloadAsync(cacheKey).ContinueWith(
                    failed => logger.LogWarning(failed.Exception, "{Source} couldn't refresh its cameras; still showing the last list.", sourceName),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            allCameras = snapshot.Cameras;
        }
        else
        {
            // Nothing yet: this view waits, but on the one shared download.
            allCameras = await DownloadAsync(cacheKey).WaitAsync(cancellationToken);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private Task<IReadOnlyList<CameraFeed>> DownloadAsync(string cacheKey)
    {
        var key = (cache, host);
        var mine = new TaskCompletionSource<IReadOnlyList<CameraFeed>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = Downloads.GetOrAdd(key, mine.Task);
        if (download != mine.Task) return download;

        _ = Task.Run(async () =>
        {
            try
            {
                // Not the asking view's token: a pan away mustn't cancel the download others share.
                var cameras = await FetchAllAsync(CancellationToken.None);
                if (cameras.Count > 0) cache.Set(cacheKey, new Snapshot(_time.GetUtcNow(), cameras), KeepFor);
                Downloads.TryRemove(new KeyValuePair<(IMemoryCache, string), Task<IReadOnlyList<CameraFeed>>>(key, mine.Task));
                mine.SetResult(cameras);
            }
            catch (Exception ex)
            {
                Downloads.TryRemove(new KeyValuePair<(IMemoryCache, string), Task<IReadOnlyList<CameraFeed>>>(key, mine.Task));
                mine.SetException(ex);
            }
        });
        return mine.Task;
    }

    // The first page says how many there are; the rest come a few at a time.
    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        var first = await PageAsync(0, cancellationToken);
        var cameras = new List<CameraFeed>((first?.Data ?? []).SelectMany(ToCameras));
        if (first?.Data is not { Count: > 0 } || first.RecordsTotal <= PageSize) return cameras;

        var pages = Math.Min(MaxPages, (first.RecordsTotal + PageSize - 1) / PageSize);
        using var gate = new SemaphoreSlim(PagesAtOnce);
        var rest = await Task.WhenAll(Enumerable.Range(1, pages - 1).Select(async page =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await PageAsync(page, cancellationToken);
            }
            // A later page failing still leaves the cameras already read.
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Source} didn't return page {Page}.", sourceName, page + 1);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }));
        foreach (var page in rest) cameras.AddRange((page?.Data ?? []).SelectMany(ToCameras));
        return cameras;
    }

    private async Task<Platform511Page?> PageAsync(int page, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PageUrl(page * PageSize));
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Platform511Page>(cancellationToken);
    }

    private IEnumerable<CameraFeed> ToCameras(Platform511Site site)
    {
        if (Point(site.LatLng?.Geography?.WellKnownText) is not { } point || !coverage.Contains(point.Latitude, point.Longitude)) yield break;
        var images = (site.Images ?? []).Where(i => !i.Disabled && !i.Blocked && i.ImageUrl?.StartsWith('/') == true).ToList();
        var siteName = FirstNonBlank(site.Location, site.Roadway) ?? $"Camera {site.Id}";
        foreach (var image in images)
        {
            var view = FirstNonBlank(image.Description);
            var label = images.Count > 1 && view is not null && !string.Equals(view, "N/A", StringComparison.OrdinalIgnoreCase)
                ? $"{siteName} - {view}"
                : siteName;
            yield return new CameraFeed(
                Id: $"511p-{host}-{image.Id}",
                Name: $"{sourceName}: {label}",
                Latitude: point.Latitude,
                Longitude: point.Longitude,
                StreamUrl: $"https://{host}{image.ImageUrl}",
                StreamKind: CameraStreamKind.Snapshot,
                SourceName: sourceName,
                SourceAttributionUrl: $"https://{host}");
        }
    }

    private string PageUrl(int start)
    {
        var query = JsonSerializer.Serialize(new
        {
            columns = new[] { new { data = (string?)null, name = string.Empty } },
            order = new[] { new { column = 0, dir = "asc" } },
            start,
            length = PageSize,
            search = new { value = string.Empty }
        });
        return $"https://{host}/List/GetData/Cameras?query={Uri.EscapeDataString(query)}&lang=en";
    }

    // "POINT (-148.975515 60.818883)": longitude first. Wisconsin lists some cameras at 0 0.
    public static (double Latitude, double Longitude)? Point(string? wellKnownText)
    {
        if (wellKnownText is null || WktPoint().Match(wellKnownText) is not { Success: true } match) return null;
        var longitude = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var latitude = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return latitude is 0 && longitude is 0 ? null : (latitude, longitude);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    [GeneratedRegex(@"POINT\s*\(\s*(-?[\d.]+)\s+(-?[\d.]+)\s*\)")]
    private static partial Regex WktPoint();

    private sealed record Platform511Page(
        [property: JsonPropertyName("recordsTotal")] int RecordsTotal,
        [property: JsonPropertyName("data")] List<Platform511Site>? Data);

    private sealed record Platform511Site(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("roadway")] string? Roadway,
        [property: JsonPropertyName("location")] string? Location,
        [property: JsonPropertyName("latLng")] Platform511LatLng? LatLng,
        [property: JsonPropertyName("images")] List<Platform511Image>? Images);

    private sealed record Platform511LatLng([property: JsonPropertyName("geography")] Platform511Geography? Geography);

    private sealed record Platform511Geography([property: JsonPropertyName("wellKnownText")] string? WellKnownText);

    private sealed record Platform511Image(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("imageUrl")] string? ImageUrl,
        [property: JsonPropertyName("disabled")] bool Disabled,
        [property: JsonPropertyName("blocked")] bool Blocked);
}
