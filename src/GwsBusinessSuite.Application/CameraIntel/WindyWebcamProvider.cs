using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CameraIntel;

// Windy's public Webcams API v3 (https://api.windy.com/webcams/docs) - a broad, free-tier-
// available aggregator of public webcams worldwide, used here for wide-but-shallow global
// coverage rather than official traffic infrastructure. Requires a free self-registered API key
// (https://api.windy.com/keys), sent as the "x-windy-api-key" header. Production gets it from the
// CAMERA_INTEL_WINDY_API_KEY GitHub secret (deploy.yml writes it into the droplet's .env).
//
// Checked against the OpenAPI schema embedded in the docs page on 07-11-26 (the first version
// guessed, and asked the list endpoint for the map endpoint's northLat/southLat parameters and read
// a string "id"):
// - GET /webcams/api/v3/webcams takes bbox={north},{east},{south},{west}, include=, limit (0-50),
//   offset (free tier: up to 1,000) and sortKey/sortDirection.
// - The answer is { total, webcams: [ { webcamId (number), title, status, location { latitude,
//   longitude, ... }, images { current { preview, ... }, ... } } ] }.
// The parsing stays defensive (skip one camera on an unexpected shape, never throw).
//
// The free tier's image URLs carry a token that expires after 10 minutes, so a view's list is kept
// for 5 minutes at most: a camera opened from it always has 5 minutes or more left. (A panel left
// open longer stops refreshing; the paid tier's tokens last 24 hours.)
public sealed class WindyWebcamProvider(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    IMemoryCache cache,
    ILogger<WindyWebcamProvider> logger) : ICameraFeedProvider
{
    private const int PageSize = 50;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public string SourceName => "Windy";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var apiKey = options.Value.WindyApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        // Windy wants -90..90 and -180..180; a view wider than the world is asked about the world.
        var north = Math.Clamp(bbox.North, -90, 90);
        var south = Math.Clamp(bbox.South, -90, 90);
        var east = Math.Clamp(bbox.East, -180, 180);
        var west = Math.Clamp(bbox.West, -180, 180);
        if (north <= south) return [];
        if (east <= west)
        {
            // Across the antimeridian: Windy's box can't wrap, so ask about the whole longitude band.
            east = 180;
            west = -180;
        }

        // Rounded so small pans reuse the same answer.
        var box = string.Join(',', new[] { north, east, south, west }.Select(v => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture)));
        var cacheKey = $"camera-intel:windy:{box}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<CameraFeed>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            // The most-viewed webcams in the view first: with 50 a call, the ones people actually watch.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"webcams/api/v3/webcams?bbox={box}&include=location,images&limit={PageSize}&sortKey=popularity&sortDirection=desc");
            request.Headers.Add("x-windy-api-key", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var webcams = Get(root, "webcams") as JsonArray;
            if (webcams is null)
            {
                logger.LogWarning("Windy webcams: response had no \"webcams\" array - raw response: {RawResponse}",
                    Truncate(root?.ToJsonString()));
                return [];
            }

            var results = new List<CameraFeed>();
            var skipped = 0;
            foreach (var webcam in webcams)
            {
                var feed = TryParse(webcam);
                if (feed is not null)
                {
                    results.Add(feed);
                }
                else
                {
                    skipped++;
                }
            }

            // Makes a "why are so few Windy cameras showing up" report diagnosable from the logs.
            if (skipped > 0)
            {
                logger.LogWarning(
                    "Windy webcams: {Skipped} of {Total} entries were skipped (inactive, or missing id/location/image) - sample raw entry: {SampleEntry}",
                    skipped, webcams.Count, Truncate(webcams.Count > 0 ? webcams[0]?.ToJsonString() : null));
            }

            cache.Set(cacheKey, (IReadOnlyList<CameraFeed>)results, CacheDuration);
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Windy webcams.");
            return [];
        }
    }

    private CameraFeed? TryParse(JsonNode? webcam)
    {
        if (webcam is not JsonObject) return null;
        var id = Text(Get(webcam, "webcamId")) ?? Text(Get(webcam, "id"));
        var latitude = Number(Get(Get(webcam, "location"), "latitude"));
        var longitude = Number(Get(Get(webcam, "location"), "longitude"));
        var imageUrl = Text(Get(Get(Get(webcam, "images"), "current"), "preview"));
        var status = Text(Get(webcam, "status"));
        if (string.IsNullOrWhiteSpace(id) || latitude is null || longitude is null
            || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var image) || image.Scheme != Uri.UriSchemeHttps
            || status is not null && status != "active")
        {
            return null;
        }

        var title = Text(Get(webcam, "title"));
        return new CameraFeed(
            Id: $"windy-{id}",
            Name: string.IsNullOrWhiteSpace(title) ? $"Windy Webcam {id}" : title,
            Latitude: latitude.Value,
            Longitude: longitude.Value,
            StreamUrl: image.AbsoluteUri,
            StreamKind: CameraStreamKind.Snapshot,
            SourceName: SourceName,
            SourceAttributionUrl: $"https://www.windy.com/webcams/{id}");
    }

    // A JSON object's property, or null when the node isn't an object (indexing one would throw).
    private static JsonNode? Get(JsonNode? node, string name) =>
        node is JsonObject json && json.TryGetPropertyValue(name, out var value) ? value : null;

    // A JSON string or number as text; anything else (or a missing node) as null.
    private static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue(out string? text) ? text
        : value.TryGetValue(out long whole) ? whole.ToString(CultureInfo.InvariantCulture)
        : value.TryGetValue(out double real) ? real.ToString(CultureInfo.InvariantCulture)
        : null
        : null;

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : null;

    private static string? Truncate(string? text) => text is null || text.Length <= 2000 ? text : text[..2000] + "…";
}
