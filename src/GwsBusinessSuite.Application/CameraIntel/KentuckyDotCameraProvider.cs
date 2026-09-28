using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// KYTC's statewide webcam layer via the Kentucky GIS server - genuinely free with no API key,
// confirmed directly. This layer's own `name`/`highway` fields are frequently null in practice
// (confirmed via a live sample) - `description` is the field that's actually always populated,
// so it's used for the camera's display name instead. Native source SR is a Kentucky-specific
// projection (WKID 102763); outSR=4326 correctly reprojects to WGS84 degrees, confirmed live.
public sealed class KentuckyDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<KentuckyDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:kytc:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "KYTC";

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
            const string url = "arcgis/rest/services/WGS84WM_Services/Ky_WebCams_WGS84WM/MapServer/0/query" +
                "?where=1=1&outFields=OBJECTID,description,snapshot&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.Snapshot))
                .Select(f => new CameraFeed(
                    Id: $"kytc-{f.Attributes.ObjectId}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.Description) ? $"KYTC Camera {f.Attributes.ObjectId}" : $"KYTC: {f.Attributes.Description}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.Snapshot!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://goky.ky.gov"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch KYTC traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] KytcAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record KytcAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("snapshot")] string? Snapshot);
}
