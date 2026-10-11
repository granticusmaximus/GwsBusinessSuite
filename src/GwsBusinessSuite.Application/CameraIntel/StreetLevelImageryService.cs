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

// Flat street photos near a point: what street view falls back to where there's no 360° imagery.
public sealed record StreetViewResult(
    double Latitude,
    double Longitude,
    IReadOnlyList<StreetImage> Images);

// One 360° panorama (Mapillary, equirectangular). CompassAngle is the direction the middle of the
// image faces, which the viewer needs to line the picture up with north.
public sealed record StreetPanorama(
    string Id,
    double Latitude,
    double Longitude,
    double CompassAngle,
    DateTimeOffset? CapturedAt,
    double DistanceMeters);

// A way to walk from the current panorama to a neighbouring one: the direction to head in
// (degrees from north) and how far it is.
public sealed record StreetPanoramaLink(string Id, double Bearing, double DistanceMeters);

// Where you're standing in Overwatch's own street view, and where you can walk to.
public sealed record StreetViewScene(StreetPanorama Current, IReadOnlyList<StreetPanoramaLink> Links);

// Overwatch's own street view ("drop the person"). The viewer is Overwatch's (street-view.js); the
// imagery is Mapillary's openly-licensed 360° panoramas (needs the free
// CameraIntel:MapillaryAccessToken), fetched by the server so the token never reaches the browser.
// Where there's no 360° imagery, flat photos from Mapillary or KartaView (no key) are offered.
public sealed class StreetLevelImageryService(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    ILogger<StreetLevelImageryService> logger)
{
    private const int MaxImages = 12;
    private const double EarthRadiusMeters = 6_371_000;
    // Tried in order until something is found: close first, then a bit wider.
    private static readonly double[] SearchRadiiMeters = [60, 250];

    // Panoramas this close or closer are the same spot; this far or further aren't "next" any more.
    private const double MinLinkMeters = 2;
    private const double MaxLinkMeters = 40;
    private static readonly double[] PanoramaSearchRadiiMeters = [60, 250];

    public bool HasPanoramaSource => !string.IsNullOrWhiteSpace(options.Value.MapillaryAccessToken);

    // The nearest 360° panorama to a point (or the one asked for), plus the neighbours you can
    // walk to. Null when there's no panorama within 250 m (or no Mapillary token).
    public async Task<StreetViewScene?> GetSceneAsync(double latitude, double longitude, string? panoramaId = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasPanoramaSource) return null;
        StreetPanorama? current = null;
        foreach (var radius in PanoramaSearchRadiiMeters)
        {
            var found = await FindPanoramasAsync(latitude, longitude, radius, cancellationToken);
            current = found.FirstOrDefault(p => p.Id == panoramaId) ?? (panoramaId is null ? found.FirstOrDefault() : null);
            if (current is not null || panoramaId is null && found.Count > 0) break;
        }
        if (current is null) return null;

        var neighbours = await FindPanoramasAsync(current.Latitude, current.Longitude, MaxLinkMeters + 10, cancellationToken);
        return new StreetViewScene(current, BuildLinks(current, neighbours));
    }

    // One link per direction (45° sectors), the nearest panorama in each - like the arrows on the
    // road in a street view app.
    public static IReadOnlyList<StreetPanoramaLink> BuildLinks(StreetPanorama current, IEnumerable<StreetPanorama> candidates) =>
        candidates
            .Where(p => p.Id != current.Id)
            .Select(p => new StreetPanoramaLink(p.Id,
                Bearing(current.Latitude, current.Longitude, p.Latitude, p.Longitude),
                DistanceMeters(current.Latitude, current.Longitude, p.Latitude, p.Longitude)))
            .Where(link => link.DistanceMeters is >= MinLinkMeters and <= MaxLinkMeters)
            .GroupBy(link => (int)Math.Round(link.Bearing / 45) % 8)
            .Select(sector => sector.OrderBy(link => link.DistanceMeters).First())
            .OrderBy(link => link.Bearing)
            .ToList();

    private async Task<IReadOnlyList<StreetPanorama>> FindPanoramasAsync(double lat, double lon, double radius, CancellationToken cancellationToken)
    {
        var (south, west, north, east) = Box(lat, lon, radius);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://graph.mapillary.com/images?fields=id,geometry,computed_geometry,compass_angle,computed_compass_angle,captured_at,is_pano&is_pano=true&bbox={west},{south},{east},{north}&limit=100&access_token={Uri.EscapeDataString(options.Value.MapillaryAccessToken)}");
        var json = await GetAsync(url, "Mapillary panoramas", TimeSpan.FromSeconds(12), cancellationToken);
        return json is null ? [] : ParsePanoramas(json, lat, lon, radius);
    }

    // {"data":[{"id","geometry"|"computed_geometry":{"coordinates":[lon,lat]},"compass_angle"|"computed_compass_angle","captured_at"(ms),"is_pano"}]}
    // Mapillary's computed (corrected) position and direction are preferred when present.
    public static IReadOnlyList<StreetPanorama> ParsePanoramas(string json, double lat, double lon, double maxMeters)
    {
        var panoramas = new List<StreetPanorama>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return panoramas;
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("is_pano", out var isPano) && isPano.ValueKind == JsonValueKind.False) continue;
                var id = String(item, "id");
                var point = Coordinates(item, "computed_geometry") ?? Coordinates(item, "geometry");
                if (id is null || !id.All(char.IsAsciiDigit) || point is not { } p) continue;
                var distance = DistanceMeters(lat, lon, p.Lat, p.Lon);
                if (distance > maxMeters) continue;
                DateTimeOffset? captured = item.TryGetProperty("captured_at", out var at) && at.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
                panoramas.Add(new StreetPanorama(id, p.Lat, p.Lon,
                    Number(item, "computed_compass_angle") ?? Number(item, "compass_angle") ?? 0, captured, distance));
            }
        }
        catch (JsonException) { }
        return panoramas.OrderBy(p => p.DistanceMeters).ToList();
    }

    // The panorama picture itself (Mapillary's 2048-wide version), for Overwatch's image endpoint.
    // Null if the id is unknown or Mapillary can't be reached.
    public async Task<(byte[] Content, string ContentType)?> GetPanoramaImageAsync(string panoramaId, CancellationToken cancellationToken = default)
    {
        if (!HasPanoramaSource || string.IsNullOrEmpty(panoramaId) || !panoramaId.All(char.IsAsciiDigit)) return null;
        var json = await GetAsync(
            $"https://graph.mapillary.com/{panoramaId}?fields=thumb_2048_url&access_token={Uri.EscapeDataString(options.Value.MapillaryAccessToken)}",
            "Mapillary image", TimeSpan.FromSeconds(12), cancellationToken);
        if (json is null) return null;
        string? imageUrl;
        try
        {
            using var doc = JsonDocument.Parse(json);
            imageUrl = String(doc.RootElement, "thumb_2048_url");
        }
        catch (JsonException) { return null; }
        if (imageUrl is null || !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await httpClient.GetAsync(imageUrl, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            return (await response.Content.ReadAsByteArrayAsync(timeout.Token), contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Street view: couldn't download panorama {PanoramaId}", panoramaId);
            return null;
        }
    }

    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = lat1 * Math.PI / 180;
        var phi2 = lat2 * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(dLon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static (double Lat, double Lon)? Coordinates(JsonElement item, string property) =>
        item.TryGetProperty(property, out var geometry) && geometry.ValueKind == JsonValueKind.Object
        && geometry.TryGetProperty("coordinates", out var coordinates) && coordinates.ValueKind == JsonValueKind.Array
        && coordinates.GetArrayLength() >= 2
            ? (coordinates[1].GetDouble(), coordinates[0].GetDouble())
            : null;

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
            images.OrderBy(image => image.DistanceMeters).Take(MaxImages).ToList());
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
