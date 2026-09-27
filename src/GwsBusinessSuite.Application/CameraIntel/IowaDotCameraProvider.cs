using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Iowa DOT's open-data camera layer (published via ArcGIS Hub as a downloadable GeoJSON) -
// genuinely free with no API key, confirmed directly: all 1,250 records had a real, populated
// ImageURL (no null/empty ones), one resolved live to a real image (HTTP 200 image/jpeg).
// The richest single confirmed source in this expansion - it's actually three device types in
// one feed (regular traffic cameras, RWIS weather-station cameras, and rest-area/parking-lot
// cameras, distinguished by the Type field), some of which also carry a VideoURL (m3u8) in
// addition to a static ImageURL - StreamKind stays Snapshot for all of them here since the
// static image is always present and this app's Hls path is unverified against this specific
// source's stream format, matching the conservative "prefer the confirmed-working field" choice
// already made for GDOT's own Url-vs-VideoUrl pick.
public sealed class IowaDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<IowaDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:iowadot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "Iowa DOT";

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
            const string url = "api/download/v1/items/c4063f200a7b4da5826e2ac86c677cf5/geojson?redirect=true&layers=0";
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>(url, cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry?.Coordinates is { Count: >= 2 } && !string.IsNullOrWhiteSpace(f.Properties.ImageUrl))
                .Select(f => new CameraFeed(
                    Id: $"iowadot-{f.Properties.DeviceId}",
                    Name: BuildName(f.Properties),
                    Latitude: f.Geometry!.Coordinates![1],
                    Longitude: f.Geometry.Coordinates[0],
                    StreamUrl: f.Properties.ImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://iowadot.gov"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Iowa DOT traffic cameras.");
            return [];
        }
    }

    private static string BuildName(IowaCameraProperties props)
    {
        var label = props.ImageName ?? props.Desc ?? $"Iowa DOT Camera {props.DeviceId}";
        return $"Iowa DOT: {label}";
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);

    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("properties")] IowaCameraProperties Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);

    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record IowaCameraProperties(
        [property: JsonPropertyName("device_id")] long DeviceId,
        [property: JsonPropertyName("Desc_")] string? Desc,
        [property: JsonPropertyName("ImageName")] string? ImageName,
        [property: JsonPropertyName("ImageURL")] string? ImageUrl,
        [property: JsonPropertyName("Type")] string? Type);
}
