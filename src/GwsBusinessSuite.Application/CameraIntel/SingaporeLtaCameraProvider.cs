using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// data.gov.sg's LTA traffic-images endpoint - key-less (verified live 2026-10-08; older notes that
// call Singapore key-gated are stale). Each image URL is a one-off snapshot that the API replaces
// about once a minute, so the list itself is only cached for a minute: re-fetching the same URL
// would keep showing the old frame.
public sealed class SingaporeLtaCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<SingaporeLtaCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:sg-lta:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public string SourceName => "Singapore LTA";

    public BoundingBox? Coverage { get; } = new(1.5, 1.15, 104.1, 103.6);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        // CameraDirectoryService already skips views outside Coverage; this guards direct callers.
        if (!bbox.Intersects(Coverage!)) return [];

        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            if (allCameras.Count > 0) cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<TrafficImagesResponse>("v1/transport/traffic-images", cancellationToken);
            var cameras = response?.Items?.FirstOrDefault()?.Cameras;
            if (cameras is null) return [];

            return cameras
                .Where(c => c.Location is not null && !string.IsNullOrWhiteSpace(c.CameraId)
                            && Uri.TryCreate(c.Image, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                .Select(c => new CameraFeed(
                    Id: $"sg-lta-{c.CameraId}",
                    Name: $"Singapore: camera {c.CameraId}",
                    Latitude: c.Location!.Latitude,
                    Longitude: c.Location.Longitude,
                    StreamUrl: c.Image!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://data.gov.sg"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Singapore LTA traffic images.");
            return [];
        }
    }

    private sealed record TrafficImagesResponse([property: JsonPropertyName("items")] List<TrafficImagesItem>? Items);

    private sealed record TrafficImagesItem([property: JsonPropertyName("cameras")] List<LtaCamera>? Cameras);

    private sealed record LtaCamera(
        [property: JsonPropertyName("camera_id")] string? CameraId,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("location")] LtaLocation? Location);

    private sealed record LtaLocation(
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude);
}
