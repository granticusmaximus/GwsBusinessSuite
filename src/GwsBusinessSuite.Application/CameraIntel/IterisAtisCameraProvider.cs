using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// States whose 511 site runs on Iteris ATIS publish their map layers as plain, key-less GeoJSON
// on {state}.cdn.iteris-atis.com - the same files the public 511 map loads. Verified live
// 2026-10-09 for South Dakota (sd511.org) and Montana (511mt.net); every other state code
// answered 403. Unlike SCDOT's flat file (SouthCarolinaDotCameraProvider), each feature here
// is a site with a nested cameras[] list (one entry per view), and cameras live in two layers:
// icons.cameras.geojson (dedicated cameras; for Montana, neighbouring states' and Canadian border
// cameras) and icons.rwis.geojson (road-weather stations, most with cameras - Montana's own
// cameras are only here). Image URLs are fixed and refreshed in place, so they're Snapshot feeds.
public sealed class IterisAtisCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<IterisAtisCameraProvider> logger,
    string stateCode,
    string sourceName,
    string sourceAttributionUrl,
    BoundingBox coverage) : ICameraFeedProvider
{
    private static readonly string[] Layers = ["icons.cameras.geojson", "icons.rwis.geojson"];
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    // A view whose image hasn't been refreshed in this long is a dead camera, not a slow one -
    // live checks found every working view updated within the last few hours.
    private static readonly TimeSpan MaxImageAge = TimeSpan.FromHours(48);

    public string SourceName => sourceName;

    public BoundingBox? Coverage => coverage;

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"camera-intel:iteris:{stateCode}:all";
        if (!cache.TryGetValue(cacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            if (allCameras.Count > 0) cache.Set(cacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        var freshAfter = DateTimeOffset.UtcNow - MaxImageAge;
        var cameras = new List<CameraFeed>();
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in Layers)
        {
            List<IterisFeature>? features;
            try
            {
                features = (await httpClient.GetFromJsonAsync<IterisFeatureCollection>(
                    $"https://{stateCode}.cdn.iteris-atis.com/geojson/icons/metadata/{layer}", cancellationToken))?.Features;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // One layer failing (e.g. RWIS) still leaves the other layer's cameras usable.
                logger.LogWarning(ex, "Failed to fetch the {Layer} layer for {Source}.", layer, sourceName);
                continue;
            }

            foreach (var feature in features ?? [])
            {
                if (feature.Geometry?.Coordinates is not { Count: >= 2 } coordinates) continue;
                var (longitude, latitude) = (coordinates[0], coordinates[1]);
                if (latitude is < -90 or > 90 || longitude is < -180 or > 180) continue;

                var siteName = FirstNonBlank(feature.Properties?.Name, feature.Properties?.Description, feature.Properties?.Route, feature.Id);
                foreach (var view in feature.Properties?.Cameras ?? [])
                {
                    if (!Uri.TryCreate(view.Image, UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps) continue;
                    if (view.UpdateTime is not { } updated || DateTimeOffset.FromUnixTimeSeconds(updated) < freshAfter) continue;
                    if (!seenImages.Add(view.Image!)) continue;

                    var viewName = FirstNonBlank(view.Description, view.Name);
                    cameras.Add(new CameraFeed(
                        Id: $"iteris-{stateCode}-{feature.Id}-{view.Id}",
                        Name: viewName is null || string.Equals(viewName, siteName, StringComparison.OrdinalIgnoreCase)
                            ? $"{sourceName}: {siteName}"
                            : $"{sourceName}: {siteName} - {viewName}",
                        Latitude: latitude,
                        Longitude: longitude,
                        StreamUrl: view.Image!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: sourceName,
                        SourceAttributionUrl: sourceAttributionUrl));
                }
            }
        }

        return cameras;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record IterisFeatureCollection([property: JsonPropertyName("features")] List<IterisFeature>? Features);

    private sealed record IterisFeature(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("geometry")] IterisGeometry? Geometry,
        [property: JsonPropertyName("properties")] IterisSite? Properties);

    private sealed record IterisGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record IterisSite(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("route")] string? Route,
        [property: JsonPropertyName("cameras")] List<IterisCameraView>? Cameras);

    private sealed record IterisCameraView(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("updateTime")] long? UpdateTime);
}
