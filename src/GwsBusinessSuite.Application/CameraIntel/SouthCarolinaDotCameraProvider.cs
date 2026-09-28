using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// SCDOT's statewide traffic camera feed, published as a plain GeoJSON file by their 511 vendor
// (Iteris) - genuinely free with no API key or User-Agent required, confirmed directly via a bare
// curl. Not an ArcGIS layer, so no outSR/pagination concerns - one flat GeoJSON file, coordinates
// already WGS84 degrees. `active`/`problem_stream` are real JSON booleans here (not ArcGIS-style
// "true"/"false" strings) - confirmed live, so they're typed as bool, not string, unlike most
// other providers in this file.
public sealed class SouthCarolinaDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<SouthCarolinaDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:scdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "SCDOT";

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
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>(
                "geojson/icons/metadata/icons.cameras.geojson", cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry?.Coordinates is { Count: >= 2 }
                    && !string.IsNullOrWhiteSpace(f.Properties.ImageUrl)
                    && f.Properties.Active
                    && !f.Properties.ProblemStream)
                .Select(f => new CameraFeed(
                    Id: $"scdot-{f.Properties.Id}",
                    Name: string.IsNullOrWhiteSpace(f.Properties.Description) ? $"SCDOT Camera {f.Properties.Id}" : $"SCDOT: {f.Properties.Description}",
                    Latitude: f.Geometry!.Coordinates![1],
                    Longitude: f.Geometry.Coordinates[0],
                    StreamUrl: f.Properties.ImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.scdot.org"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch SCDOT traffic cameras.");
            return [];
        }
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);

    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("properties")] ScDotCameraProperties Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);

    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record ScDotCameraProperties(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("image_url")] string? ImageUrl,
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("problem_stream")] bool ProblemStream);
}
