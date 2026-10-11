using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CameraIntel;

// One street-level photo near a dropped point.
public sealed record StreetImage(
    string Source,
    string ImageUrl,
    double Latitude,
    double Longitude,
    double DistanceMeters,
    DateTimeOffset? CapturedAt,
    double? CompassAngle,
    // The photo on its own site (Mapillary / KartaView), for attribution and a bigger viewer.
    string ViewUrl);

public sealed record StreetViewResult(
    double Latitude,
    double Longitude,
    IReadOnlyList<StreetImage> Images,
    string GoogleStreetViewUrl);

// Overwatch's "drop the person" street view. Google doesn't allow Street View imagery inside
// another app without a paid Maps API key, so the in-app photos come from free sources -
// Mapillary (needs the free CameraIntel:MapillaryAccessToken) and KartaView (no key) - and the
// result always carries a standard Google Maps link that opens Street View at the same spot.
public sealed class StreetLevelImageryService(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    ILogger<StreetLevelImageryService> logger)
{
    private const int MaxImages = 12;
    private const double EarthRadiusMeters = 6_371_000;
    // Tried in order until something is found: close first, then a bit wider.
    private static readonly double[] SearchRadiiMeters = [60, 250];

    public static string GoogleStreetViewUrl(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture,
            $"https://www.google.com/maps/@?api=1&map_action=pano&viewpoint={latitude:0.######},{longitude:0.######}");

    public async Task<StreetViewResult> FindNearAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        var images = new List<StreetImage>();
        foreach (var radius in SearchRadiiMeters)
        {
            images.AddRange(await MapillaryAsync(latitude, longitude, radius, cancellationToken));
            if (images.Count == 0) images.AddRange(await KartaViewAsync(latitude, longitude, radius, cancellationToken));
            if (images.Count > 0) break;
        }

        return new StreetViewResult(latitude, longitude,
            images.OrderBy(image => image.DistanceMeters).Take(MaxImages).ToList(),
            GoogleStreetViewUrl(latitude, longitude));
    }

    private async Task<IReadOnlyList<StreetImage>> MapillaryAsync(double lat, double lon, double radius, CancellationToken cancellationToken)
    {
        var token = options.Value.MapillaryAccessToken;
        if (string.IsNullOrWhiteSpace(token)) return [];
        var (south, west, north, east) = Box(lat, lon, radius);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://graph.mapillary.com/images?fields=id,thumb_1024_url,captured_at,compass_angle,geometry&bbox={west},{south},{east},{north}&limit=50&access_token={Uri.EscapeDataString(token)}");
        var json = await GetAsync(url, "Mapillary", TimeSpan.FromSeconds(12), cancellationToken);
        return json is null ? [] : ParseMapillary(json, lat, lon, radius);
    }

    private async Task<IReadOnlyList<StreetImage>> KartaViewAsync(double lat, double lon, double radius, CancellationToken cancellationToken)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.openstreetcam.org/2.0/photo/?lat={lat:0.######}&lng={lon:0.######}&radius={radius:0}");
        // KartaView's API is sometimes very slow; don't hold the panel up for long.
        var json = await GetAsync(url, "KartaView", TimeSpan.FromSeconds(8), cancellationToken);
        return json is null ? [] : ParseKartaView(json, lat, lon, radius);
    }

    private async Task<string?> GetAsync(string url, string source, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await httpClient.GetAsync(url, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Street view: {Source} answered {Status}", source, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(timeoutSource.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Street view: {Source} didn't answer", source);
            return null;
        }
    }

    // Mapillary Graph API: {"data":[{"id","thumb_1024_url","captured_at"(ms),"compass_angle","geometry":{"coordinates":[lon,lat]}}]}
    public static IReadOnlyList<StreetImage> ParseMapillary(string json, double lat, double lon, double maxMeters)
    {
        var images = new List<StreetImage>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return images;
            foreach (var item in data.EnumerateArray())
            {
                var id = String(item, "id");
                var image = String(item, "thumb_1024_url");
                if (id is null || image is null || !item.TryGetProperty("geometry", out var geometry)
                    || !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.GetArrayLength() < 2) continue;
                var (photoLat, photoLon) = (coordinates[1].GetDouble(), coordinates[0].GetDouble());
                var distance = DistanceMeters(lat, lon, photoLat, photoLon);
                if (distance > maxMeters) continue;
                DateTimeOffset? captured = item.TryGetProperty("captured_at", out var at) && at.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
                images.Add(new StreetImage("Mapillary", image, photoLat, photoLon, distance, captured,
                    Number(item, "compass_angle"), $"https://www.mapillary.com/app/?pKey={Uri.EscapeDataString(id)}"));
            }
        }
        catch (JsonException) { }
        return images;
    }

    // KartaView v2: {"result":{"data":[{"id","sequenceId","sequenceIndex","lat","lng","heading","shotDate","fileurlProc"|"fileurl"|"fileurlTh"}]}}
    // Numbers sometimes arrive as strings, so both are read.
    public static IReadOnlyList<StreetImage> ParseKartaView(string json, double lat, double lon, double maxMeters)
    {
        var images = new List<StreetImage>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array) return images;
            foreach (var item in data.EnumerateArray())
            {
                var image = String(item, "fileurlProc") ?? String(item, "fileurl") ?? String(item, "fileurlTh");
                if (image is null || Number(item, "lat") is not { } photoLat || Number(item, "lng") is not { } photoLon) continue;
                if (image.StartsWith("//", StringComparison.Ordinal)) image = "https:" + image;
                if (!image.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                var distance = DistanceMeters(lat, lon, photoLat, photoLon);
                if (distance > maxMeters) continue;
                DateTimeOffset? captured = DateTime.TryParse(String(item, "shotDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var shot) ? new DateTimeOffset(shot, TimeSpan.Zero) : null;
                var sequence = String(item, "sequenceId");
                var index = String(item, "sequenceIndex");
                var viewUrl = sequence is not null && index is not null
                    ? $"https://kartaview.org/details/{Uri.EscapeDataString(sequence)}/{Uri.EscapeDataString(index)}"
                    : "https://kartaview.org";
                images.Add(new StreetImage("KartaView", image, photoLat, photoLon, distance, captured, Number(item, "heading"), viewUrl));
            }
        }
        catch (JsonException) { }
        return images;
    }

    // "north", "northeast", ... for a compass angle in degrees.
    public static string? Facing(double? angle) => angle is { } value
        ? new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" }[(int)Math.Round(((value % 360) + 360) % 360 / 45) % 8]
        : null;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static (double South, double West, double North, double East) Box(double lat, double lon, double meters)
    {
        var dLat = meters / 111_320d;
        var dLon = meters / (111_320d * Math.Max(0.1, Math.Cos(lat * Math.PI / 180)));
        return (lat - dLat, lon - dLon, lat + dLat, lon + dLon);
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        } : null;

    private static double? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        } : null;
}
