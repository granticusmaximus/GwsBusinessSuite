using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Transport for NSW publishes this public GeoJSON camera feed under CC BY. It contains only
// still-image URLs, so Overwatch must present it as snapshots and retain Transport for NSW credit.
public sealed class TransportForNswCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<TransportForNswCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:transport-nsw:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "Transport for NSW";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? cameras) || cameras is null)
        {
            cameras = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, cameras, CacheDuration);
        }

        return cameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>("cameras/traffic-cam.json", cancellationToken);
            return collection?.Features?
                .Where(feature => feature.Geometry?.Coordinates is { Count: >= 2 } && Uri.IsWellFormedUriString(feature.Properties.Href, UriKind.Absolute))
                .Select(feature => new CameraFeed(
                    $"transport-nsw-{feature.Properties.Id ?? $"{feature.Properties.Region}:{feature.Properties.Title}:{feature.Properties.View}"}",
                    $"NSW: {feature.Properties.Title ?? "Traffic camera"}",
                    feature.Geometry!.Coordinates![1],
                    feature.Geometry.Coordinates[0],
                    feature.Properties.Href!,
                    CameraStreamKind.Snapshot,
                    SourceName,
                    "https://opendata.transport.nsw.gov.au/dataset/live-traffic-cameras"))
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Transport for NSW traffic cameras.");
            return [];
        }
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);
    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("properties")] NswCameraProperties Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);
    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);
    private sealed record NswCameraProperties(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("view")] string? View,
        [property: JsonPropertyName("href")] string? Href);
}
