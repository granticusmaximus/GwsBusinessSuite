using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// NYC DOT's public traffic camera API - genuinely free with no API key or registration of any
// kind, confirmed directly: a plain GET returns ~980 cameras as a flat JSON array, each with a
// stable id, lat/lon, and a dedicated per-camera image endpoint
// (webcams.nyctmc.org/api/cameras/{id}/image) - fetched and confirmed live (HTTP 200,
// image/jpeg). City-scale, not statewide, same category as Datumfeed's own aggregated cities.
public sealed class NycDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NycDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:nycdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "NYC DOT";

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
            var cameras = await httpClient.GetFromJsonAsync<List<NycCamera>>("api/cameras/", cancellationToken);
            if (cameras is null) return [];

            return cameras
                .Where(c => c is { Latitude: not 0, Longitude: not 0 } && !string.IsNullOrWhiteSpace(c.ImageUrl)
                    && !string.Equals(c.IsOnline, "false", StringComparison.OrdinalIgnoreCase))
                .Select(c => new CameraFeed(
                    Id: $"nycdot-{c.Id}",
                    Name: string.IsNullOrWhiteSpace(c.Name) ? $"NYC DOT Camera {c.Id}" : $"NYC DOT: {c.Name}",
                    Latitude: c.Latitude,
                    Longitude: c.Longitude,
                    StreamUrl: c.ImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://webcams.nyctmc.org"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch NYC DOT traffic cameras.");
            return [];
        }
    }

    // Confirmed live: this API's own JSON keys are camelCase (id/name/latitude/longitude/
    // isOnline/imageUrl), not PascalCase - without explicit [JsonPropertyName] here,
    // GetFromJsonAsync's case-sensitive default deserialization would silently leave every field
    // at its default (0.0/null), making this provider return zero cameras in production despite
    // looking correct at a glance. Same gotcha already documented on NztaCameraAttributes above.
    private sealed record NycCamera(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude,
        [property: JsonPropertyName("isOnline")] string? IsOnline,
        [property: JsonPropertyName("imageUrl")] string? ImageUrl);
}
