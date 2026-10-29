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
    BoundingBox coverage) : ICameraFeedProvider
{
    private const int PageSize = 100;
    // Utah, the biggest, is 21 pages; this stops a site that misreports its total from looping.
    private const int MaxPages = 60;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public string SourceName => sourceName;

    public BoundingBox? Coverage => coverage;

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"camera-intel:511-platform:{host}";
        if (!cache.TryGetValue(cacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            if (allCameras.Count > 0) cache.Set(cacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        var cameras = new List<CameraFeed>();
        for (var page = 0; page < MaxPages; page++)
        {
            Platform511Page? result;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, PageUrl(page * PageSize));
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                result = await response.Content.ReadFromJsonAsync<Platform511Page>(cancellationToken);
            }
            // The first page failing is the source being down (the directory records it); a later
            // one still leaves the cameras already read.
            catch (Exception ex) when (page > 0 && ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(ex, "{Source} stopped answering after {Count} cameras.", sourceName, cameras.Count);
                break;
            }

            foreach (var site in result?.Data ?? [])
                cameras.AddRange(ToCameras(site));
            if (result?.Data is not { Count: > 0 } || (page + 1) * PageSize >= result.RecordsTotal) break;
        }
        return cameras;
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
