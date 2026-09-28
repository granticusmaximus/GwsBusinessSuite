using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// DelDOT's traffic camera layer via Delaware FirstMap ArcGIS - genuinely free with no API key,
// confirmed directly. This layer only publishes stream URLs (RTMP/RTSP/M3U8/M3U8S/MSSMOOTH), no
// static-image field at all - M3U8S resolves to a real HLS playlist
// (video.deldot.gov:443/live/{ID}.stream/playlist.m3u8), confirmed live, so this is the first
// Overwatch camera provider to use CameraStreamKind.Hls rather than Snapshot (confirmed the
// client-side HLS playback path in tactical-globe.js's renderStream is real, not a stub, before
// choosing this).
public sealed class DelawareDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<DelawareDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:deldot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "DelDOT";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string url = "arcgis/rest/services/Transportation/DE_TMC_Traffic_Feeds/FeatureServer/1/query" +
                "?where=1=1&outFields=OBJECTID,ID,TITLE,COUNTY,M3U8S&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.M3U8S))
                .Select(f => new CameraFeed(
                    Id: $"deldot-{f.Attributes.Id}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.Title) ? $"DelDOT: {f.Attributes.Id}" : $"DelDOT: {f.Attributes.Title}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.M3U8S!,
                    StreamKind: CameraStreamKind.Hls,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://deldot.gov"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch DelDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] DelDotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record DelDotAttributes(
        [property: JsonPropertyName("ID")] string? Id,
        [property: JsonPropertyName("TITLE")] string? Title,
        [property: JsonPropertyName("M3U8S")] string? M3U8S);
}
