using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Estonia's Transport Administration publishes its road-camera layer through the key-less
// Tark Tee ArcGIS service. Verified live 2026-10-08: the layer returns WGS84 points and a
// relative JPEG path; https://tarktee.ee/images/{image_path} serves the current full-size image.
public sealed class EstoniaRoadCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<EstoniaRoadCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:estonia-tarktee:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public string SourceName => "Estonia Tark Tee";

    public BoundingBox? Coverage { get; } = new(60, 57.3, 28.3, 21.5);

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
            const string request = "tarktee/rest/services/tram/road_cameras/MapServer/0/query" +
                "?where=1%3D1&outFields=objectid%2Csite_name%2Cimage_path&returnGeometry=true&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisResponse>(request, cancellationToken);
            if (response?.Features is null) return [];

            return response.Features
                .Where(feature => feature.Attributes is not null && feature.Geometry is not null
                    && feature.Geometry.Y is >= 57.3 and <= 60
                    && feature.Geometry.X is >= 21.5 and <= 28.3
                    && IsSafeImagePath(feature.Attributes.ImagePath))
                .Select(feature => new CameraFeed(
                    Id: $"estonia-tarktee-{feature.Attributes!.ObjectId}",
                    Name: $"Estonia: {feature.Attributes.SiteName?.Trim() ?? $"camera {feature.Attributes.ObjectId}"}",
                    Latitude: feature.Geometry!.Y,
                    Longitude: feature.Geometry.X,
                    StreamUrl: new Uri(httpClient.BaseAddress!, $"images/{feature.Attributes.ImagePath}").AbsoluteUri,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://tarktee.ee/"))
                .GroupBy(camera => camera.Id)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Estonia Tark Tee road cameras.");
            return [];
        }
    }

    private static bool IsSafeImagePath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith('/')
        && !value.Contains("..", StringComparison.Ordinal)
        && value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    private sealed record ArcGisResponse(
        [property: JsonPropertyName("features")] List<ArcGisFeature>? Features,
        [property: JsonPropertyName("error")] JsonElement? Error);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] CameraAttributes? Attributes,
        [property: JsonPropertyName("geometry")] PointGeometry? Geometry);

    private sealed record CameraAttributes(
        [property: JsonPropertyName("objectid")] long ObjectId,
        [property: JsonPropertyName("site_name")] string? SiteName,
        [property: JsonPropertyName("image_path")] string? ImagePath);

    private sealed record PointGeometry(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);
}
