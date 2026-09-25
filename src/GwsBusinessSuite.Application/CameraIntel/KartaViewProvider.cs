using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// KartaView (formerly OpenStreetCam) - a free, community-contributed worldwide street-level
// photo platform, genuinely no API key or registration required (verified directly during
// implementation: GET api.openstreetcam.org/2.0/photo/?lat=&lng=&radius= returned real photo
// data with no auth header at all). Coverage is user-contributed and globally uneven (dense in
// parts of Europe, sparse-to-none in many US areas), unlike Mapillary's more even worldwide
// coverage - the two are complementary, not redundant.
//
// The API is radius-from-a-point, not bbox-based - confirmed directly that a too-wide radius
// causes the API's own query to time out ("Narrow your filter (smaller bbox...)"), so the radius
// derived from the current view is capped at a conservative maximum rather than scaling with an
// arbitrarily wide zoomed-out view.
public sealed class KartaViewProvider(
    HttpClient httpClient,
    ILogger<KartaViewProvider> logger) : ICameraFeedProvider
{
    private const double MaxRadiusMeters = 300;
    private const double EarthRadiusMeters = 6_371_000;

    public string SourceName => "KartaView";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var centerLat = (bbox.North + bbox.South) / 2.0;
        var centerLon = (bbox.East + bbox.West) / 2.0;
        var radius = Math.Min(MaxRadiusMeters, HaversineMeters(centerLat, centerLon, bbox.North, bbox.East));
        if (radius < 1)
        {
            return [];
        }

        try
        {
            var url = $"2.0/photo/?lat={centerLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&lng={centerLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&radius={radius.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var photos = root?["result"]?["data"]?.AsArray();
            if (photos is null)
            {
                return [];
            }

            var results = new List<CameraFeed>();
            foreach (var photo in photos)
            {
                var feed = TryParse(photo);
                if (feed is not null)
                {
                    results.Add(feed);
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch KartaView photos.");
            return [];
        }
    }

    private static CameraFeed? TryParse(JsonNode? photo)
    {
        var id = photo?["id"]?.GetValue<string>();
        var lat = TryParseDouble(photo?["lat"]);
        var lon = TryParseDouble(photo?["lng"]);
        var thumbUrl = photo?["fileurlTh"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || lat is null || lon is null || string.IsNullOrWhiteSpace(thumbUrl))
        {
            return null;
        }

        return new CameraFeed(
            Id: $"kartaview-{id}",
            Name: $"KartaView street photo #{id}",
            Latitude: lat.Value,
            Longitude: lon.Value,
            StreamUrl: thumbUrl,
            StreamKind: CameraStreamKind.Snapshot,
            SourceName: "KartaView",
            SourceAttributionUrl: "https://kartaview.org");
    }

    // KartaView's own JSON returns lat/lng as strings, not numbers - confirmed directly against
    // a live response.
    private static double? TryParseDouble(JsonNode? node)
    {
        var text = node?.GetValue<string>();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
            (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
