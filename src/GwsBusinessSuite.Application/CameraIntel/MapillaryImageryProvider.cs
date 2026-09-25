using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CameraIntel;

// Mapillary (owned by Meta) - a free, worldwide, community-contributed street-level imagery
// platform with genuinely broad global coverage, complementing KartaView's own (sparser, more
// Europe-concentrated) coverage. Uses Mapillary's real Graph API
// (graph.mapillary.com/images?fields=...&bbox=west,south,east,north, the same Facebook/Meta
// Graph API shape Mapillary itself is built on - confirmed directly: an unauthenticated request
// returns a real "Invalid OAuth 2.0 Access Token" MLYApiException, not a 404, confirming the
// endpoint/parameter shape is correct). Requires a free, self-registered access token
// (mapillary.com/developer) - same optional, absent-disables-the-feature convention as
// WindyWebcamProvider.
public sealed class MapillaryImageryProvider(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    ILogger<MapillaryImageryProvider> logger) : ICameraFeedProvider
{
    // A safety cap, matching DatumfeedCameraProvider's own precedent - keeps a wide-zoomed view
    // from requesting an unbounded number of images at once.
    private const int MaxResultsPerQuery = 100;

    public string SourceName => "Mapillary";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var accessToken = options.Value.MapillaryAccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return [];
        }

        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var bboxParam = string.Join(',',
                bbox.West.ToString(inv), bbox.South.ToString(inv), bbox.East.ToString(inv), bbox.North.ToString(inv));
            var url = $"images?fields=id,thumb_256_url,geometry&bbox={bboxParam}&limit={MaxResultsPerQuery}&access_token={Uri.EscapeDataString(accessToken)}";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var images = root?["data"]?.AsArray();
            if (images is null)
            {
                return [];
            }

            var results = new List<CameraFeed>();
            foreach (var image in images)
            {
                var feed = TryParse(image);
                if (feed is not null)
                {
                    results.Add(feed);
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Mapillary images.");
            return [];
        }
    }

    private static CameraFeed? TryParse(JsonNode? image)
    {
        var id = image?["id"]?.GetValue<string>();
        var thumbUrl = image?["thumb_256_url"]?.GetValue<string>();
        // Mapillary's geometry field is a GeoJSON Point: {"type":"Point","coordinates":[lon,lat]}.
        var coordinates = image?["geometry"]?["coordinates"]?.AsArray();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(thumbUrl) || coordinates is null || coordinates.Count < 2)
        {
            return null;
        }

        var lon = coordinates[0]?.GetValue<double>();
        var lat = coordinates[1]?.GetValue<double>();
        if (lon is null || lat is null)
        {
            return null;
        }

        return new CameraFeed(
            Id: $"mapillary-{id}",
            Name: $"Mapillary street photo #{id}",
            Latitude: lat.Value,
            Longitude: lon.Value,
            StreamUrl: thumbUrl,
            StreamKind: CameraStreamKind.Snapshot,
            SourceName: "Mapillary",
            SourceAttributionUrl: "https://www.mapillary.com");
    }
}
