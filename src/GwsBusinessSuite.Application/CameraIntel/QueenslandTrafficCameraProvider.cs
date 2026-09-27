using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Queensland, Australia's public traffic camera feed (data.qldtraffic.qld.gov.au) - genuinely
// free with no API key at all, confirmed directly: a real camera's image_url resolved live
// (HTTP 200 image/jpeg, Last-Modified about a minute before the request). Plain GeoJSON,
// CRS EPSG:7844 (GDA2020) - close enough to WGS84 for map-pin placement that no reprojection is
// needed (the same "accepted small gap" already documented for Illinois' own NAD83 data). The
// strongest international find during this expansion: this same org also publishes a real,
// live incidents feed (QueenslandTrafficIncidentProvider) from the identical zero-auth source.
public sealed class QueenslandTrafficCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<QueenslandTrafficCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:qldtraffic:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "QLD Traffic";

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
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>("webcameras.geojson", cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry?.Coordinates is { Count: >= 2 } && !string.IsNullOrWhiteSpace(f.Properties.ImageUrl))
                .Select(f => new CameraFeed(
                    Id: $"qldtraffic-{f.Properties.Id}",
                    Name: string.IsNullOrWhiteSpace(f.Properties.Description) ? $"QLD Traffic Camera {f.Properties.Id}" : $"QLD: {f.Properties.Description}",
                    Latitude: f.Geometry!.Coordinates![1],
                    Longitude: f.Geometry.Coordinates[0],
                    StreamUrl: f.Properties.ImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.qldtraffic.qld.gov.au"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Queensland traffic cameras.");
            return [];
        }
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);

    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("properties")] QldCameraProperties Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);

    // coordinates is GeoJSON's own standard [longitude, latitude] order.
    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record QldCameraProperties(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("image_url")] string? ImageUrl);
}
