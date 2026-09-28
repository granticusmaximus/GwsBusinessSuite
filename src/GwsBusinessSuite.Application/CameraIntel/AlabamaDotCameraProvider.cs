using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// ALDOT/ALEA's own public camera API (the same one powering their official ALGO Traffic app) -
// genuinely free with no API key, confirmed directly. Not an ArcGIS layer - a plain JSON array
// response, coordinates already WGS84 degrees. `accessLevel` distinguishes public cameras from
// "FirstResponder"-only ones (confirmed live) - only "Public" is surfaced here. This is an
// undocumented API (no published developer portal found), so treat it as stable-in-practice
// rather than contractually guaranteed, same caveat as GDOT's own camera feed.
public sealed class AlabamaDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<AlabamaDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:aldot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "ALDOT";

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
            var cameras = await httpClient.GetFromJsonAsync<List<AlgoCamera>>("v4.0/cameras", cancellationToken);
            if (cameras is null) return [];

            return cameras
                .Where(c => string.Equals(c.AccessLevel, "Public", StringComparison.OrdinalIgnoreCase)
                    && c.Location is not null
                    && !string.IsNullOrWhiteSpace(c.SnapshotImageUrl))
                .Select(c => new CameraFeed(
                    Id: $"aldot-{c.Id}",
                    Name: BuildName(c),
                    Latitude: c.Location!.Latitude,
                    Longitude: c.Location.Longitude,
                    StreamUrl: c.SnapshotImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.algotraffic.com"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch ALDOT traffic cameras.");
            return [];
        }
    }

    private static string BuildName(AlgoCamera c)
    {
        var route = c.Location!.DisplayRouteDesignator?.Trim();
        var crossStreet = c.Location.DisplayCrossStreet?.Trim();
        if (string.IsNullOrWhiteSpace(route)) return $"ALDOT Camera {c.Id}";
        return string.IsNullOrWhiteSpace(crossStreet) ? $"ALDOT: {route}" : $"ALDOT: {route} - {crossStreet}";
    }

    private sealed record AlgoCamera(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("location")] AlgoLocation? Location,
        [property: JsonPropertyName("accessLevel")] string? AccessLevel,
        [property: JsonPropertyName("snapshotImageUrl")] string? SnapshotImageUrl);

    private sealed record AlgoLocation(
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude,
        [property: JsonPropertyName("displayRouteDesignator")] string? DisplayRouteDesignator,
        [property: JsonPropertyName("displayCrossStreet")] string? DisplayCrossStreet);
}
