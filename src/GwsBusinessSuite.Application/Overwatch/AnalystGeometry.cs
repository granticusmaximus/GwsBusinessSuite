namespace GwsBusinessSuite.Application.Overwatch;

// Overwatch's analyst tools (13-11-26): distances and bearings along clicked points, and range
// rings, on the WGS84 sphere (great-circle, about 0.3% from the ellipsoid: fine for analysis, not
// for surveying). Rings are polylines, so the web globe and the Mac app's map draw them the same way.
public static class AnalystGeometry
{
    public const double EarthRadiusKm = 6371.0088;
    public const int RingSegments = 72;
    public const int MaxRings = 6;
    public const double MaxRingKm = 5000;

    public sealed record Leg(double Kilometres, double BearingDegrees);

    public static double Kilometres(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // The initial great-circle bearing from the first point to the second, degrees from true north.
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var y = Math.Sin(Rad(lon2 - lon1)) * Math.Cos(Rad(lat2));
        var x = Math.Cos(Rad(lat1)) * Math.Sin(Rad(lat2)) - Math.Sin(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Cos(Rad(lon2 - lon1));
        return (Deg(Math.Atan2(y, x)) + 360) % 360;
    }

    public static IReadOnlyList<Leg> Legs(IReadOnlyList<(double Lat, double Lon)> points) =>
        points.Zip(points.Skip(1), (a, b) => new Leg(Kilometres(a.Lat, a.Lon, b.Lat, b.Lon), Bearing(a.Lat, a.Lon, b.Lat, b.Lon))).ToList();

    // The point at a distance and bearing from a start (for drawing rings).
    public static (double Lat, double Lon) Destination(double lat, double lon, double km, double bearing)
    {
        var d = km / EarthRadiusKm;
        var lat1 = Rad(lat);
        var b = Rad(bearing);
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(b));
        var lon2 = Rad(lon) + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return (Deg(lat2), (Deg(lon2) + 540) % 360 - 180);
    }

    public static IReadOnlyList<(double Lat, double Lon)> Ring(double lat, double lon, double km) =>
        Enumerable.Range(0, RingSegments + 1).Select(i => Destination(lat, lon, km, i * 360.0 / RingSegments)).ToList();

    // "5, 25 100" -> [5, 25, 100]: positive, at most MaxRingKm, at most MaxRings, in order.
    public static IReadOnlyList<double> ParseRings(string? text) =>
        (text ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var km) ? km : 0)
            .Where(km => km > 0 && km <= MaxRingKm)
            .Distinct().Order().Take(MaxRings).ToList();

    public static string Describe(Leg leg) =>
        $"{leg.Kilometres:0.##} km ({leg.Kilometres * 0.621371:0.##} mi) at {leg.BearingDegrees:0}° {SunPosition.Compass(leg.BearingDegrees)}";

    private static double Rad(double degrees) => degrees * Math.PI / 180;
    private static double Deg(double radians) => radians * 180 / Math.PI;
}
