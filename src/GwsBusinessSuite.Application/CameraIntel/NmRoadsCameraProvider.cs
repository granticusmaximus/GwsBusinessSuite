using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// NMRoads (NMDOT's official 511 site) - verified live 2026-10-09, no key. GetCameraInfo is the
// same plain-JSON camera list nmroads.com's own map loads (185 cameras). Each record's
// snapshotFile is http-only (ss.nmroads.com refuses TLS), which an https page can't show, so the
// image comes from the service's own GetCameraImage?cameraName= endpoint instead - the one the
// map's camera popup uses, served over https. Sampled 25 cameras: 22 distinct current JPEGs and
// 3 empty bodies, which the globe already shows as an unavailable feed. The list's ArcGIS
// "Cameras" layer (services6.arcgis.com/iUUetf0q4cK7iW5Q) needs a token, so it isn't used.
public sealed class NmRoadsCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NmRoadsCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:nmroads:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public string SourceName => "NMRoads";

    public BoundingBox? Coverage { get; } = new(North: 37.1, South: 31.3, East: -103.0, West: -109.1);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
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
            var response = await httpClient.GetFromJsonAsync<NmRoadsCameraList>("RealMapWAR/GetCameraInfo", cancellationToken);
            return (response?.Cameras ?? [])
                .Where(c => c.Enabled && !c.Mobile
                            && !string.IsNullOrWhiteSpace(c.Name)
                            && c.Latitude is >= 31 and <= 37.5 && c.Longitude is >= -109.5 and <= -102.5)
                .GroupBy(c => c.Name, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(c => new CameraFeed(
                    Id: $"nmroads-{c.Name}",
                    Name: $"NMRoads: {(string.IsNullOrWhiteSpace(c.Title) ? c.Name : c.Title.Trim())}",
                    Latitude: c.Latitude!.Value,
                    Longitude: c.Longitude!.Value,
                    StreamUrl: $"{httpClient.BaseAddress}RealMapWAR/GetCameraImage?ts=0&cameraName={Uri.EscapeDataString(c.Name!)}",
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.nmroads.com"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch NMRoads cameras.");
            return [];
        }
    }

    private sealed record NmRoadsCameraList([property: JsonPropertyName("cameraInfo")] List<NmRoadsCamera>? Cameras);

    private sealed record NmRoadsCamera(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("lat")] double? Latitude,
        [property: JsonPropertyName("lon")] double? Longitude,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("mobile")] bool Mobile);
}
