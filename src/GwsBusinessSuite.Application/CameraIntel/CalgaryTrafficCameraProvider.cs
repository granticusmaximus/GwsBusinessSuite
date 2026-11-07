using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// The City of Calgary's traffic cameras, from its open-data portal (Socrata dataset k7p9-kppz,
// key-less). Verified live 07-11-26: 217 cameras, each with a point and a current JPEG on
// trafficcam.calgary.ca that the city refreshes in place.
public sealed class CalgaryTrafficCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<CalgaryTrafficCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:calgary:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    public string SourceName => "City of Calgary";

    public BoundingBox? Coverage { get; } = new(51.3, 50.8, -113.8, -114.4);

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
            if (JsonNode.Parse(await httpClient.GetStringAsync("resource/k7p9-kppz.json?$limit=2000", cancellationToken)) is not JsonArray rows) return [];

            var cameras = new List<CameraFeed>();
            foreach (var row in rows)
            {
                var image = Text(Get(Get(row, "camera_url"), "url"));
                if (Get(Get(row, "point"), "coordinates") is not JsonArray { Count: >= 2 } point
                    || Number(point[0]) is not { } longitude || Number(point[1]) is not { } latitude
                    || latitude is < 50.6 or > 51.5 || longitude is < -114.6 or > -113.6
                    || !Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                var location = Text(Get(row, "camera_location"))?.Trim();
                var quadrant = Text(Get(row, "quadrant"))?.Trim();
                cameras.Add(new CameraFeed(
                    Id: $"calgary-{Path.GetFileNameWithoutExtension(uri.AbsolutePath)}",
                    Name: "Calgary: " + (string.IsNullOrEmpty(location) ? Text(Get(Get(row, "camera_url"), "description")) ?? "Traffic camera" : location)
                          + (string.IsNullOrEmpty(quadrant) ? string.Empty : $" ({quadrant})"),
                    Latitude: latitude,
                    Longitude: longitude,
                    StreamUrl: uri.AbsoluteUri,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://data.calgary.ca/Transportation-Transit/Traffic-Cameras/k7p9-kppz"));
            }
            return cameras.GroupBy(camera => camera.StreamUrl).Select(group => group.First()).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch City of Calgary traffic cameras.");
            return [];
        }
    }

    // A JSON object's property, or null when the node isn't an object (indexing one would throw).
    private static JsonNode? Get(JsonNode? node, string name) =>
        node is JsonObject json && json.TryGetPropertyValue(name, out var value) ? value : null;

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
