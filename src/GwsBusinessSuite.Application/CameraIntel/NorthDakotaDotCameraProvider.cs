using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// NDDOT's statewide traffic camera feed, published as a plain GeoJSON file - genuinely free with
// no API key, confirmed directly. Unlike every other camera provider in this file, this feed is
// cluster-shaped: one GeoJSON Feature is a physical site (with one shared lat/lon) whose
// `Cameras` property array holds multiple individual camera views at that site (e.g. "West",
// "North", "East", "Pavement" facing the same intersection) - confirmed live. Each entry in that
// array is flattened into its own CameraFeed here, sharing the parent Feature's coordinates,
// since Overwatch's model has no concept of "multiple cameras at one exact pin."
public sealed class NorthDakotaDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NorthDakotaDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:nddot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "NDDOT";

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
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>("geojson_nc/cameras.json", cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            var results = new List<CameraFeed>();
            foreach (var feature in features)
            {
                var coords = feature.Geometry?.Coordinates;
                var cameras = feature.Properties?.Cameras;
                if (coords is not { Count: >= 2 } || cameras is null) continue;

                var lon = coords[0];
                var lat = coords[1];
                foreach (var cam in cameras)
                {
                    if (string.IsNullOrWhiteSpace(cam.FullPath)) continue;
                    results.Add(new CameraFeed(
                        Id: $"nddot-{feature.Id}-{Uri.EscapeDataString(cam.FullPath)}",
                        Name: string.IsNullOrWhiteSpace(cam.Description) ? $"NDDOT Camera ({feature.Properties!.Region})" : $"NDDOT: {cam.Description}",
                        Latitude: lat,
                        Longitude: lon,
                        StreamUrl: cam.FullPath!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://travel.dot.nd.gov"));
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch NDDOT traffic cameras.");
            return [];
        }
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);

    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("properties")] NdCameraClusterProperties? Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);

    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record NdCameraClusterProperties(
        [property: JsonPropertyName("Region")] string? Region,
        [property: JsonPropertyName("Cameras")] List<NdCamera>? Cameras);

    private sealed record NdCamera(
        [property: JsonPropertyName("Description")] string? Description,
        [property: JsonPropertyName("FullPath")] string? FullPath);
}
