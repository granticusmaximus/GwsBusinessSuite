using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// HDOT's Oahu traffic camera layer via ArcGIS - genuinely free with no API key, confirmed
// directly (a real Akamai CDN snapshot resolved live, HTTP 200 image/jpeg). The location list
// itself is a frozen 2019/2021 snapshot (confirmed via the service's own edit-date metadata) -
// this app has no way to detect cameras added or removed since, but the snapshot image URLs
// still resolve live for the ones on record. Oahu-only; no equivalent layer was found for the
// other Hawaiian islands.
public sealed class HawaiiDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<HawaiiDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:hdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "HDOT";

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
            const string url = "arcgis/rest/services/HawaiiTrafficCameras/FeatureServer/0/query" +
                "?where=1=1&outFields=OBJECTID,URL,Camera_Description&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.Url))
                .Select(f => new CameraFeed(
                    Id: $"hdot-{f.Attributes.ObjectId}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.CameraDescription) ? $"HDOT Camera {f.Attributes.ObjectId}" : $"HDOT: {f.Attributes.CameraDescription}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.Url!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://hidot.hawaii.gov"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch HDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] HdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record HdotAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("URL")] string? Url,
        [property: JsonPropertyName("Camera_Description")] string? CameraDescription);
}
