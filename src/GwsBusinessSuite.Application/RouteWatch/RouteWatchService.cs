using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Geocoding;
using GwsBusinessSuite.Application.MessageSigns;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RouteWatch;

public sealed record RoutePoint(double Latitude, double Longitude);

public sealed record RouteCamera(CameraFeed Camera, double MilesAlong, double MilesOff);

public sealed record RouteIncident(TrafficIncident Incident, double MilesAlong, double MilesOff);

public sealed record RouteSign(MessageSign Sign, double MilesAlong, double MilesOff);

// One leg between consecutive stops of a trip.
public sealed record RouteLeg(string From, string To, double DistanceMiles, double DurationMinutes, double StartsAtMile);

// The resolved position of each stop (in order), so the globe can mark them.
public sealed record RouteStop(string Label, double Latitude, double Longitude);

public sealed record RouteWatchResult(
    string From,
    string To,
    double DistanceMiles,
    double DurationMinutes,
    IReadOnlyList<RoutePoint> Path,
    IReadOnlyList<RouteCamera> Cameras,
    IReadOnlyList<RouteIncident> Incidents,
    IReadOnlyList<WeatherAlert> Alerts,
    string? Error = null,
    IReadOnlyList<RouteLeg>? Legs = null,
    IReadOnlyList<RouteStop>? Stops = null,
    IReadOnlyList<RouteSign>? Signs = null)
{
    public static RouteWatchResult Failed(string from, string to, string error) => new(from, to, 0, 0, [], [], [], [], error);
}

// "What's on my drive from A to B": geocodes both ends with the page's own geocoder, routes them
// with the public OSRM server (key-less, verified 2026-10-08; its fair-use policy is fine for
// one user-initiated request at a time), then keeps only the cameras, incidents and NWS alerts
// that actually touch the road, ordered by how far along the drive they are.
public sealed class RouteWatchService(
    HttpClient httpClient,
    IGeocodingService geocoding,
    CameraDirectoryService cameras,
    TrafficIncidentDirectoryService incidents,
    INwsAlertsService alerts,
    ILogger<RouteWatchService> logger,
    MessageSignDirectoryService? signs = null)
{
    private const string OsrmRouteUrl = "https://router.project-osrm.org/route/v1/driving/";
    public const double CameraCorridorMiles = 0.5;
    public const double IncidentCorridorMiles = 1.0;
    // Signs stand at the roadside; this keeps those on the route's own road, not a parallel one.
    public const double SignCorridorMiles = 0.3;
    public const int MaxCameras = 60;
    private const double MaxRouteMiles = 1200;

    public const int MaxStops = 10;

    public Task<RouteWatchResult> WatchAsync(string from, string to, CancellationToken cancellationToken = default) =>
        WatchTripAsync([from, to], cancellationToken);

    // A trip: start, any stops, destination - routed in order as one drive.
    public async Task<RouteWatchResult> WatchTripAsync(IReadOnlyList<string> stops, CancellationToken cancellationToken = default)
    {
        var names = (stops ?? []).Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0).ToList();
        var from = names.FirstOrDefault() ?? string.Empty;
        var to = names.Count > 1 ? names[^1] : string.Empty;
        if (names.Count < 2) return RouteWatchResult.Failed(from, to, "Enter both a start and a destination.");
        if (names.Count > MaxStops) return RouteWatchResult.Failed(from, to, $"A trip can have up to {MaxStops} stops.");

        var points = await Task.WhenAll(names.Select(n => ResolveAsync(n, cancellationToken)));
        for (var i = 0; i < names.Count; i++)
        {
            if (points[i] is null) return RouteWatchResult.Failed(from, to, $"Couldn't find \"{names[i]}\".");
        }
        var resolved = points.Select(p => p!).ToList();

        (IReadOnlyList<RoutePoint> Path, double Meters, double Seconds, IReadOnlyList<(double Meters, double Seconds)> Legs)? route;
        try
        {
            var coordinates = string.Join(';', resolved.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Longitude},{p.Latitude}")));
            var url = $"{OsrmRouteUrl}{coordinates}?overview=full&geometries=geojson";
            route = ParseOsrmRoute(await httpClient.GetStringAsync(url, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Route watch: OSRM routing failed");
            return RouteWatchResult.Failed(from, to, "The routing service didn't answer. Try again in a moment.");
        }
        if (route is not { } r || r.Path.Count < 2) return RouteWatchResult.Failed(from, to, "No drivable route was found between those places.");
        var miles = r.Meters / 1609.344;
        var legs = new List<RouteLeg>();
        var startsAt = 0d;
        for (var i = 0; i < r.Legs.Count && i + 1 < names.Count; i++)
        {
            var legMiles = r.Legs[i].Meters / 1609.344;
            legs.Add(new RouteLeg(names[i], names[i + 1], legMiles, r.Legs[i].Seconds / 60, startsAt));
            startsAt += legMiles;
        }
        var routeStops = names.Select((n, i) => new RouteStop(n, resolved[i].Latitude, resolved[i].Longitude)).ToList();
        if (miles > MaxRouteMiles) return RouteWatchResult.Failed(from, to, $"That route is {miles:N0} miles; route watch handles drives up to {MaxRouteMiles:N0} miles.");

        var bbox = Expand(BoundsOf(r.Path), IncidentCorridorMiles);
        var camerasTask = cameras.GetCamerasInBoundingBoxAsync(bbox, cancellationToken);
        var incidentsTask = incidents.GetIncidentsInBoundingBoxAsync(bbox, cancellationToken);
        var alertsTask = alerts.GetActiveAlertsAsync(bbox, cancellationToken);
        var signsTask = signs?.GetSignsInBoundingBoxAsync(bbox, cancellationToken) ?? Task.FromResult<IReadOnlyList<MessageSign>>([]);
        await Task.WhenAll(camerasTask, incidentsTask, alertsTask, signsTask);

        var index = new RouteIndex(r.Path);
        var routeCameras = camerasTask.Result
            .Select(c => (Camera: c, Hit: index.Locate(c.Latitude, c.Longitude)))
            .Where(x => x.Hit.MilesOff <= CameraCorridorMiles)
            .OrderBy(x => x.Hit.MilesAlong)
            .Select(x => new RouteCamera(x.Camera, x.Hit.MilesAlong, x.Hit.MilesOff))
            .ToList();
        var routeIncidents = incidentsTask.Result
            .Select(i => (Incident: i, Hit: index.Locate(i.Latitude, i.Longitude)))
            .Where(x => x.Hit.MilesOff <= IncidentCorridorMiles)
            .OrderBy(x => x.Hit.MilesAlong)
            .Select(x => new RouteIncident(x.Incident, x.Hit.MilesAlong, x.Hit.MilesOff))
            .ToList();
        var routeAlerts = alertsTask.Result.Where(a => TouchesRoute(a, r.Path)).ToList();
        var routeSigns = signsTask.Result
            .Select(sign => (Sign: sign, Hit: index.Locate(sign.Latitude, sign.Longitude)))
            .Where(x => x.Hit.MilesOff <= SignCorridorMiles)
            .OrderBy(x => x.Hit.MilesAlong)
            .Select(x => new RouteSign(x.Sign, x.Hit.MilesAlong, x.Hit.MilesOff))
            .ToList();

        return new RouteWatchResult(from, to, miles, r.Seconds / 60, r.Path,
            ThinAlongRoute(routeCameras, MaxCameras), routeIncidents, routeAlerts, null, legs, routeStops, routeSigns);
    }

    // "32.46, -83.61" (from the map picker, or typed) is used as-is; anything else is geocoded.
    private async Task<GeocodeResult?> ResolveAsync(string text, CancellationToken ct) =>
        TryParseCoordinates(text) ?? await geocoding.GeocodeAsync(text, ct);

    public static GeocodeResult? TryParseCoordinates(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
               && lat is >= -90 and <= 90 && lon is >= -180 and <= 180
            ? new GeocodeResult(lat, lon)
            : null;
    }

    public static (IReadOnlyList<RoutePoint> Path, double Meters, double Seconds, IReadOnlyList<(double Meters, double Seconds)> Legs)? ParseOsrmRoute(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("code", out var code) || code.GetString() != "Ok") return null;
        var routes = doc.RootElement.GetProperty("routes");
        if (routes.GetArrayLength() == 0) return null;
        var route = routes[0];
        var path = route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
            .Select(c => new RoutePoint(c[1].GetDouble(), c[0].GetDouble()))
            .ToList();
        var legs = route.TryGetProperty("legs", out var legsEl) && legsEl.ValueKind == JsonValueKind.Array
            ? legsEl.EnumerateArray().Select(l => (l.GetProperty("distance").GetDouble(), l.GetProperty("duration").GetDouble())).ToList()
            : [];
        return (path, route.GetProperty("distance").GetDouble(), route.GetProperty("duration").GetDouble(), legs);
    }

    // A long drive can pass hundreds of cameras; keep an even spread along the route instead of
    // the first N, always including the first and last.
    public static IReadOnlyList<RouteCamera> ThinAlongRoute(IReadOnlyList<RouteCamera> ordered, int max)
    {
        if (ordered.Count <= max || max < 2) return ordered;
        var step = (ordered.Count - 1) / (double)(max - 1);
        return Enumerable.Range(0, max).Select(i => ordered[(int)Math.Round(i * step)]).Distinct().ToList();
    }

    public static bool TouchesRoute(WeatherAlert alert, IReadOnlyList<RoutePoint> path) =>
        alert.Rings.Any(ring => ring.Count >= 3 && path.Any(p => PointInRing(p.Latitude, p.Longitude, ring)));

    private static bool PointInRing(double lat, double lon, IReadOnlyList<(double Longitude, double Latitude)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    private static BoundingBox BoundsOf(IReadOnlyList<RoutePoint> path) =>
        new(path.Max(p => p.Latitude), path.Min(p => p.Latitude), path.Max(p => p.Longitude), path.Min(p => p.Longitude));

    private static BoundingBox Expand(BoundingBox b, double miles)
    {
        var dLat = miles / 69.0;
        var dLon = miles / (69.0 * Math.Max(0.2, Math.Cos((b.North + b.South) / 2 * Math.PI / 180)));
        return new(b.North + dLat, b.South - dLat, b.East + dLon, b.West - dLon);
    }

    // Distance from a point to the route and how far along the route its nearest point is.
    // Local equirectangular projection per segment - accurate to well under 1% at corridor scale.
    public sealed class RouteIndex
    {
        private readonly IReadOnlyList<RoutePoint> _path;
        private readonly double[] _cumulativeMiles;

        public RouteIndex(IReadOnlyList<RoutePoint> path)
        {
            _path = path;
            _cumulativeMiles = new double[path.Count];
            for (var i = 1; i < path.Count; i++)
                _cumulativeMiles[i] = _cumulativeMiles[i - 1] + CivicDistance(path[i - 1], path[i]);
        }

        public double TotalMiles => _cumulativeMiles[^1];

        public (double MilesAlong, double MilesOff) Locate(double lat, double lon)
        {
            var best = (Along: 0d, Off: double.MaxValue);
            for (var i = 1; i < _path.Count; i++)
            {
                var a = _path[i - 1];
                var b = _path[i];
                var cos = Math.Cos((a.Latitude + b.Latitude) / 2 * Math.PI / 180);
                double ax = 0, ay = 0;
                var bx = (b.Longitude - a.Longitude) * 69.172 * cos;
                var by = (b.Latitude - a.Latitude) * 69.0;
                var px = (lon - a.Longitude) * 69.172 * cos;
                var py = (lat - a.Latitude) * 69.0;
                var len2 = bx * bx + by * by;
                var t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * bx + (py - ay) * by) / len2, 0, 1);
                var dx = px - t * bx;
                var dy = py - t * by;
                var off = Math.Sqrt(dx * dx + dy * dy);
                if (off < best.Off) best = (_cumulativeMiles[i - 1] + t * Math.Sqrt(len2), off);
            }
            return best;
        }

        private static double CivicDistance(RoutePoint a, RoutePoint b)
        {
            var cos = Math.Cos((a.Latitude + b.Latitude) / 2 * Math.PI / 180);
            var dx = (b.Longitude - a.Longitude) * 69.172 * cos;
            var dy = (b.Latitude - a.Latitude) * 69.0;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
