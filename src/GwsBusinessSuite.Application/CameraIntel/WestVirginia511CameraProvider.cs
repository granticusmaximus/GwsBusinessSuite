using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// WV511 (WVDOH's official 511 site) - verified live 2026-10-09, no key. Every camera is live
// video only (the still-image markup is commented out in its own data), so there are no
// snapshots to use. Two key-less public calls:
//   1. /wsvc/gmap.asmx/buildCamerasJSONjs - a JavaScript file whose `camera_data = {...}` object
//      lists all 133 cameras with coordinates and a description.
//   2. /flowplayeri.aspx?CAMID={id} - the site's own player page, which names the camera's HLS
//      manifest. The host differs per camera (vtc1/vtc2/vtc3.roadsummary.com) and a manifest only
//      answers on its own host (others return 204), so the URL can't be derived from the id.
// The manifests are CORS-open (Access-Control-Allow-Origin: *) and play natively in Chromium and
// WebKit under this app's CSP (media-src https:) - checked with Playwright. On 2026-10-09, 112 of
// 133 answered 200 and the rest 404, so a camera is only listed when its manifest answers.
public sealed partial class WestVirginia511CameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<WestVirginia511CameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:wv511:all";
    // Resolving takes ~20s live (two requests per camera), so it's done rarely: the camera set
    // and each camera's stream host barely change, and a stream that drops in the meantime just
    // shows the globe's own "feed unavailable" state.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private const int MaxConcurrentLookups = 8;
    // Resolving the list costs ~2 requests per camera, so concurrent views share one resolution.
    private static readonly SemaphoreSlim FetchLock = new(1, 1);

    public string SourceName => "WV511";

    public BoundingBox? Coverage { get; } = new(North: 40.7, South: 37.1, East: -77.6, West: -82.7);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            await FetchLock.WaitAsync(cancellationToken);
            try
            {
                if (!cache.TryGetValue(CacheKey, out allCameras) || allCameras is null)
                {
                    allCameras = await FetchAllAsync(cancellationToken);
                    if (allCameras.Count > 0) cache.Set(CacheKey, allCameras, CacheDuration);
                }
            }
            finally
            {
                FetchLock.Release();
            }
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        List<Wv511Camera> listed;
        try
        {
            var script = await httpClient.GetStringAsync("wsvc/gmap.asmx/buildCamerasJSONjs", cancellationToken);
            listed = ParseCameraData(script);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or FormatException)
        {
            logger.LogWarning(ex, "Failed to fetch the WV511 camera list.");
            return [];
        }

        using var throttle = new SemaphoreSlim(MaxConcurrentLookups);
        var resolved = await Task.WhenAll(listed.Select(async camera =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await ResolveAsync(camera, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        }));
        return resolved.OfType<CameraFeed>().ToList();
    }

    private async Task<CameraFeed?> ResolveAsync(Wv511Camera camera, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(camera.Id) || !CameraIdPattern().IsMatch(camera.Id)) return null;
        if (!double.TryParse(camera.Latitude, System.Globalization.CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(camera.Longitude, System.Globalization.CultureInfo.InvariantCulture, out var longitude))
        {
            return null;
        }

        try
        {
            var player = await httpClient.GetStringAsync($"flowplayeri.aspx?CAMID={Uri.EscapeDataString(camera.Id)}", cancellationToken);
            var manifest = ManifestPattern().Match(player);
            if (!manifest.Success) return null;

            using var probe = await httpClient.GetAsync(manifest.Value, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (probe.StatusCode != HttpStatusCode.OK) return null;

            return new CameraFeed(
                Id: $"wv511-{camera.Id}",
                Name: $"WV511: {DescribeLocation(camera)}",
                Latitude: latitude,
                Longitude: longitude,
                StreamUrl: manifest.Value,
                StreamKind: CameraStreamKind.Hls,
                SourceName: SourceName,
                SourceAttributionUrl: "https://wv511.org");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Skipping WV511 camera {CameraId}.", camera.Id);
            return null;
        }
    }

    // The data is a JavaScript file, not JSON: read just the object literal after `camera_data =`.
    private static List<Wv511Camera> ParseCameraData(string script)
    {
        var marker = script.IndexOf("camera_data", StringComparison.Ordinal);
        var start = marker < 0 ? -1 : script.IndexOf('{', marker);
        if (start < 0) throw new FormatException("The WV511 camera script has no camera_data object.");

        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(script[start..]));
        return JsonSerializer.Deserialize<Wv511CameraData>(ref reader)?.Cameras ?? [];
    }

    // The description is HTML like `<div id="camDescription">[BER]I-81 @ 0.5<span ...>West
    // Virginia DOT</span></div>...`: keep the location text and drop the county code prefix.
    private static string DescribeLocation(Wv511Camera camera)
    {
        var text = DescriptionPattern().Match(camera.Description ?? string.Empty) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : null;
        text = text is null ? null : CountyPrefixPattern().Replace(text, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? camera.Title ?? camera.Id! : text;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex CameraIdPattern();

    [GeneratedRegex(@"https://[A-Za-z0-9.-]+\.roadsummary\.com/rtplive/[A-Za-z0-9_-]+/playlist\.m3u8")]
    private static partial Regex ManifestPattern();

    [GeneratedRegex(@"id=""camDescription"">([^<]*)<")]
    private static partial Regex DescriptionPattern();

    [GeneratedRegex(@"^\[[A-Z]{2,4}\]\s*")]
    private static partial Regex CountyPrefixPattern();

    private sealed record Wv511CameraData([property: JsonPropertyName("cams")] List<Wv511Camera>? Cameras);

    private sealed record Wv511Camera(
        [property: JsonPropertyName("md5")] string? Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("start_lat")] string? Latitude,
        [property: JsonPropertyName("start_lng")] string? Longitude);
}
