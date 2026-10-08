using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Vegagerðin (Icelandic Road and Coastal Administration) publishes every road webcam as one
// key-less JSON list - verified live 2026-10-08: 496 cameras, each with WGS84 coordinates
// (Breidd/Lengd) and a direct JPEG (Slod) that the agency refreshes in place.
public sealed class IcelandRoadCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<IcelandRoadCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:vegagerdin:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public string SourceName => "Vegagerðin";

    public BoundingBox? Coverage { get; } = new(67, 63, -13, -25);

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
            var cameras = await httpClient.GetFromJsonAsync<List<VegagerdinCamera>>("api/vefmyndavelar2014_1", cancellationToken);
            if (cameras is null) return [];

            return cameras
                .Where(c => c.Latitude is >= 62 and <= 68 && c.Longitude is >= -26 and <= -12
                            && Uri.TryCreate(c.ImageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                .GroupBy(c => c.ImageUrl)
                .Select(g => g.First())
                .Select(c => new CameraFeed(
                    Id: $"vegagerdin-{c.Number}-{Path.GetFileNameWithoutExtension(c.ImageUrl)}",
                    Name: string.IsNullOrWhiteSpace(c.Description) ? $"Iceland: {c.Name}" : $"Iceland: {c.Description}",
                    Latitude: c.Latitude!.Value,
                    Longitude: c.Longitude!.Value,
                    StreamUrl: c.ImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.vegagerdin.is"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Vegagerðin road cameras.");
            return [];
        }
    }

    private sealed record VegagerdinCamera(
        [property: JsonPropertyName("Maelist_nr")] long Number,
        [property: JsonPropertyName("Myndavel")] string? Name,
        [property: JsonPropertyName("Skyring")] string? Description,
        [property: JsonPropertyName("Slod")] string? ImageUrl,
        [property: JsonPropertyName("Breidd")] double? Latitude,
        [property: JsonPropertyName("Lengd")] double? Longitude);
}
