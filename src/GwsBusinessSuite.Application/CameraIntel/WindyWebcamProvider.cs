using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CameraIntel;

// Windy's public Webcams API v3 (https://api.windy.com/webcams/docs) - a broad, free-tier-
// available aggregator of public webcams worldwide, used here for wide-but-shallow global
// coverage rather than official traffic infrastructure. Requires a free self-registered API key
// (https://api.windy.com/webcams/dev), sent as the "x-windy-api-key" header.
//
// The exact nested shape of a webcam's "images"/"player" object is inferred from partial public
// documentation, not a fully confirmed live response (Windy's full v3 schema docs were not
// completely accessible during implementation) - parsing below is deliberately defensive
// (JsonNode + safe navigation, skip-on-miss rather than throw) so an unexpected shape drops that
// one camera instead of the whole call. Verify this against a live registered key and adjust the
// property paths below if Windy's real response differs.
//
// The free tier's image URLs are token-secured and expire after 10 minutes, so nothing here
// caches a resolved image URL past this call - see CameraDirectoryService's short cache window.
public sealed class WindyWebcamProvider(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    ILogger<WindyWebcamProvider> logger) : ICameraFeedProvider
{
    public string SourceName => "Windy";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var apiKey = options.Value.WindyApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"webcams/api/v3/webcams?northLat={bbox.North}&southLat={bbox.South}&eastLon={bbox.East}&westLon={bbox.West}&include=location,images&limit=50");
            request.Headers.Add("x-windy-api-key", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var webcams = root?["webcams"]?.AsArray();
            if (webcams is null)
            {
                logger.LogWarning(
                    "Windy webcams: response had no \"webcams\" array - raw response: {RawResponse}",
                    root?.ToJsonString());
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

            // The exact response shape was never confirmed against a live key (see the class
            // comment) - this makes a real-world "why are so few Windy cameras showing up"
            // report diagnosable from the logs instead of requiring another guess at the schema.
            if (skipped > 0)
            {
                logger.LogWarning(
                    "Windy webcams: {Skipped} of {Total} entries were skipped (missing id/location/image field) - " +
                    "sample raw entry: {SampleEntry}",
                    skipped, webcams.Count, webcams.Count > 0 ? webcams[0]?.ToJsonString() : null);
            }

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
        var id = webcam?["id"]?.GetValue<string>();
        var latitude = webcam?["location"]?["latitude"]?.GetValue<double>();
        var longitude = webcam?["location"]?["longitude"]?.GetValue<double>();
        var imageUrl = webcam?["images"]?["current"]?["preview"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || latitude is null || longitude is null || string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var title = webcam?["title"]?.GetValue<string>();
        return new CameraFeed(
            Id: $"windy-{id}",
            Name: string.IsNullOrWhiteSpace(title) ? $"Windy Webcam {id}" : title,
            Latitude: latitude.Value,
            Longitude: longitude.Value,
            StreamUrl: imageUrl,
            StreamKind: CameraStreamKind.Snapshot,
            SourceName: SourceName,
            SourceAttributionUrl: "https://www.windy.com/webcams");
    }
}
