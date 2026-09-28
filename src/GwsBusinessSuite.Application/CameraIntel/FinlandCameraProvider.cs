using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Fintraffic's Digitraffic weathercam API (Finland) - genuinely free with no API key, confirmed
// directly (a real weathercam.digitraffic.fi/{presetId}.jpg resolved live). Real, non-obvious
// gotcha confirmed live: this API returns HTTP 406 unless the request explicitly sends an
// Accept-Encoding: gzip header (configured on this provider's own HttpClient registration in
// DependencyInjection.cs, with AutomaticDecompression enabled on the handler so the response body
// still comes back as plain JSON) - Digitraffic's own docs also warn against sending an
// Authorization header at all, since there is genuinely no auth. Each station Feature carries
// multiple camera "presets" (different physical camera angles at the same station) - flattened
// into one CameraFeed per preset here, sharing the parent station's coordinates, same pattern as
// NorthDakotaDotCameraProvider's own camera-cluster flattening.
public sealed class FinlandCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<FinlandCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:fintraffic:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "Fintraffic";

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
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>("api/weathercam/v1/stations", cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            var results = new List<CameraFeed>();
            foreach (var feature in features)
            {
                var coords = feature.Geometry?.Coordinates;
                var presets = feature.Properties?.Presets;
                if (coords is not { Count: >= 2 } || presets is null) continue;

                var lon = coords[0];
                var lat = coords[1];
                var stationName = feature.Properties!.Name ?? feature.Id ?? "";
                foreach (var preset in presets.Where(p => p.InCollection))
                {
                    if (string.IsNullOrWhiteSpace(preset.Id)) continue;
                    results.Add(new CameraFeed(
                        Id: $"fintraffic-{preset.Id}",
                        Name: $"Fintraffic: {stationName}",
                        Latitude: lat,
                        Longitude: lon,
                        StreamUrl: $"https://weathercam.digitraffic.fi/{preset.Id}.jpg",
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://www.digitraffic.fi"));
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Fintraffic weathercams.");
            return [];
        }
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);

    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("properties")] FintrafficStationProperties? Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);

    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record FintrafficStationProperties(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("presets")] List<FintrafficPreset>? Presets);

    private sealed record FintrafficPreset(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("inCollection")] bool InCollection);
}
