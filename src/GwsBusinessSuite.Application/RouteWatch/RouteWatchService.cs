using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Geocoding;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RouteWatch;

public sealed record RoutePoint(double Latitude, double Longitude);

public sealed record RouteCamera(CameraFeed Camera, double MilesAlong, double MilesOff);

public sealed record RouteIncident(TrafficIncident Incident, double MilesAlong, double MilesOff);

public sealed record RouteWatchResult(
    string From,
    string To,
    double DistanceMiles,
    double DurationMinutes,
    IReadOnlyList<RoutePoint> Path,
    IReadOnlyList<RouteCamera> Cameras,
    IReadOnlyList<RouteIncident> Incidents,
    IReadOnlyList<WeatherAlert> Alerts,
    string? Error = null)
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
    ILogger<RouteWatchService> logger)
{
    private const string OsrmRouteUrl = "https://router.project-osrm.org/route/v1/driving/";
    public const double CameraCorridorMiles = 0.5;
    public const double IncidentCorridorMiles = 1.0;
    public const int MaxCameras = 60;
    private const double MaxRouteMiles = 1200;

    public async Task<RouteWatchResult> WatchAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        from = (from ?? string.Empty).Trim();
        to = (to ?? string.Empty).Trim();
        if (from.Length == 0 || to.Length == 0) return RouteWatchResult.Failed(from, to, "Enter both a start and a destination.");

        var startTask = geocoding.GeocodeAsync(from, cancellationToken);
        var endTask = geocoding.GeocodeAsync(to, cancellationToken);
        await Task.WhenAll(startTask, endTask);
        if (startTask.Result is not { } start) return RouteWatchResult.Failed(from, to, $"Couldn't find \"{from}\".");
        if (endTask.Result is not { } end) return RouteWatchResult.Failed(from, to, $"Couldn't find \"{to}\".");

        (IReadOnlyList<RoutePoint> Path, double Meters, double Seconds)? route;
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{OsrmRouteUrl}{start.Longitude},{start.Latitude};{end.Longitude},{end.Latitude}?overview=full&geometries=geojson");
            route = ParseOsrmRoute(await httpClient.GetStringAsync(url, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Route watch: OSRM routing failed");
            return RouteWatchResult.Failed(from, to, "The routing service didn't answer. Try again in a moment.");
        }
        if (route is not { } r || r.Path.Count < 2) return RouteWatchResult.Failed(from, to, "No drivable route was found between those places.");
        var miles = r.Meters / 1609.344;
        if (miles > MaxRouteMiles) return RouteWatchResult.Failed(from, to, $"That route is {miles:N0} miles; route watch handles drives up to {MaxRouteMiles:N0} miles.");

        var bbox = Expand(BoundsOf(r.Path), IncidentCorridorMiles);
        var camerasTask = cameras.GetCamerasInBoundingBoxAsync(bbox, cancellationToken);
        var incidentsTask = incidents.GetIncidentsInBoundingBoxAsync(bbox, cancellationToken);
        var alertsTask = alerts.GetActiveAlertsAsync(bbox, cancellationToken);
        await Task.WhenAll(camerasTask, incidentsTask, alertsTask);

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

        return new RouteWatchResult(from, to, miles, r.Seconds / 60, r.Path,
            ThinAlongRoute(routeCameras, MaxCameras), routeIncidents, routeAlerts);
    }

    public static (IReadOnlyList<RoutePoint> Path, double Meters, double Seconds)? ParseOsrmRoute(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("code", out var code) || code.GetString() != "Ok") return null;
        var routes = doc.RootElement.GetProperty("routes");
        if (routes.GetArrayLength() == 0) return null;
        var route = routes[0];
        var path = route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
            .Select(c => new RoutePoint(c[1].GetDouble(), c[0].GetDouble()))
            .ToList();
        return (path, route.GetProperty("distance").GetDouble(), route.GetProperty("duration").GetDouble());
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
