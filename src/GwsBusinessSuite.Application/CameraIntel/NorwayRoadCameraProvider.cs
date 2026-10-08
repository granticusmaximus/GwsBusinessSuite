using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Statens vegvesen's public traffic site reads this key-less first-party endpoint. Verified live
// 2026-10-08: 790 measurement sites, 134 active cameras with public HLS manifests and CORS-enabled
// video segments. Still-image URLs currently return 500, so only proven-working HLS cameras are
// exposed; a later refresh can add snapshots when the agency restores them.
public sealed class NorwayRoadCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NorwayRoadCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:norway-vegvesen:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public string SourceName => "Statens vegvesen";

    public BoundingBox? Coverage { get; } = new(72, 57, 32, 3);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(
        BoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
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
            var response = await httpClient.GetFromJsonAsync<MeasurementSitesResponse>(
                "weather-information/measurement-sites", cancellationToken);
            if (response?.MeasurementSites is null) return [];

            return response.MeasurementSites
                .Where(site => site.Location?.Geometry?.Coordinates is { Count: >= 2 })
                .SelectMany(site => (site.Cameras ?? [])
                    .Where(camera => !string.IsNullOrWhiteSpace(camera.Id)
                        && string.Equals(camera.Status, "OK", StringComparison.OrdinalIgnoreCase)
                        && IsPublicHls(camera.VideoUrl))
                    .Select(camera => new CameraFeed(
                        Id: $"norway-vegvesen-{camera.Id}",
                        Name: BuildName(site.Name, camera.OrientationDescription),
                        Latitude: site.Location!.Geometry!.Coordinates![1],
                        Longitude: site.Location.Geometry.Coordinates[0],
                        StreamUrl: camera.VideoUrl!,
                        StreamKind: CameraStreamKind.Hls,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://www.vegvesen.no/trafikk/")))
                .Where(camera => Coverage!.Contains(camera.Latitude, camera.Longitude))
                .GroupBy(camera => camera.Id)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Statens vegvesen road cameras.");
            return [];
        }
    }

    private static string BuildName(string? siteName, string? orientation) =>
        string.IsNullOrWhiteSpace(orientation)
            ? $"Norway: {siteName?.Trim() ?? "road camera"}"
            : $"Norway: {siteName?.Trim() ?? "road camera"} toward {orientation.Trim()}";

    private static bool IsPublicHls(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "kamera.vegvesen.no", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private sealed record MeasurementSitesResponse(
        [property: JsonPropertyName("measurementSites")] List<MeasurementSite>? MeasurementSites,
        [property: JsonPropertyName("metadata")] JsonElement? Metadata);

    private sealed record MeasurementSite(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("location")] SiteLocation? Location,
        [property: JsonPropertyName("cameras")] List<RoadCamera>? Cameras,
        [property: JsonPropertyName("weatherData")] JsonElement? WeatherData,
        [property: JsonPropertyName("siteType")] string? SiteType);

    private sealed record SiteLocation(
        [property: JsonPropertyName("geometry")] PointGeometry? Geometry,
        [property: JsonPropertyName("heightAboveSeaLevel")] double? HeightAboveSeaLevel,
        [property: JsonPropertyName("roadLinkReference")] JsonElement? RoadLinkReference,
        [property: JsonPropertyName("road")] JsonElement? Road,
        [property: JsonPropertyName("county")] JsonElement? County);

    private sealed record PointGeometry(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record RoadCamera(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("orientationDescription")] string? OrientationDescription,
        [property: JsonPropertyName("stillImageUrl")] string? StillImageUrl,
        [property: JsonPropertyName("videoUrl")] string? VideoUrl,
        [property: JsonPropertyName("yrUrl")] string? YrUrl,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("lastUpdated")] DateTimeOffset? LastUpdated);
}
