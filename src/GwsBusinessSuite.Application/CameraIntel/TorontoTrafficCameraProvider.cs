using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// The City of Toronto's traffic cameras, from its open-data portal under the Open Government
// Licence - Toronto. Verified live 07-11-26: 336 cameras as WGS84 GeoJSON (MultiPoint geometry),
// each with a current JPEG on opendata.toronto.ca (IMAGEURL) that the city refreshes in place.
public sealed class TorontoTrafficCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<TorontoTrafficCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:toronto:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    // The "Traffic Camera List - 4326.geojson" resource of the portal's traffic-cameras dataset.
    public const string ListPath =
        "dataset/a3309088-5fd4-4d34-8297-77c8301840ac/resource/4a568300-c7f8-496d-b150-dff6f5dc6d4f/download/traffic-camera-list-4326.geojson";

    public string SourceName => "City of Toronto";

    public BoundingBox? Coverage { get; } = new(43.9, 43.55, -79.1, -79.65);

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
            var root = JsonNode.Parse(await httpClient.GetStringAsync(ListPath, cancellationToken));
            if (Get(root, "features") is not JsonArray features) return [];

            var cameras = new List<CameraFeed>();
            foreach (var feature in features)
            {
                if (Get(feature, "properties") is not JsonObject properties || Point(Get(feature, "geometry")) is not { } point) continue;
                var (latitude, longitude) = point;
                var image = Text(properties["IMAGEURL"]);
                if (latitude is < 43.4 or > 44.1 || longitude is < -79.8 or > -78.9
                    || !Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                var id = Text(properties["REC_ID"]) ?? Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                var main = Tidy(Text(properties["MAINROAD"]));
                var cross = Tidy(Text(properties["CROSSROAD"]));
                cameras.Add(new CameraFeed(
                    Id: $"toronto-{id}",
                    Name: "Toronto: " + (main, cross) switch
                    {
                        ({ Length: > 0 }, { Length: > 0 }) => $"{main} at {cross}",
                        ({ Length: > 0 }, _) => main,
                        _ => $"Camera {id}"
                    },
                    Latitude: latitude,
                    Longitude: longitude,
                    StreamUrl: uri.AbsoluteUri,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://open.toronto.ca/dataset/traffic-cameras/"));
            }
            return cameras.GroupBy(camera => camera.StreamUrl).Select(group => group.First()).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch City of Toronto traffic cameras.");
            return [];
        }
    }

    // A Point, or the first point of a MultiPoint, as (latitude, longitude).
    private static (double Latitude, double Longitude)? Point(JsonNode? geometry)
    {
        var coordinates = Get(geometry, "coordinates");
        if (coordinates is JsonArray { Count: > 0 } outer && outer[0] is JsonArray inner) coordinates = inner;
        return coordinates is JsonArray { Count: >= 2 } pair && Number(pair[0]) is { } longitude && Number(pair[1]) is { } latitude
            ? (latitude, longitude)
            : null;
    }

    // A JSON object's property, or null when the node isn't an object (indexing one would throw).
    private static JsonNode? Get(JsonNode? node, string name) =>
        node is JsonObject json && json.TryGetPropertyValue(name, out var value) ? value : null;

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : null;

    private static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue(out string? text) ? text
        : value.TryGetValue(out long whole) ? whole.ToString(CultureInfo.InvariantCulture)
        : null
        : null;

    // "YORK ST" -> "York St"; the list is in capitals.
    private static string Tidy(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.Trim().ToLowerInvariant());
}
