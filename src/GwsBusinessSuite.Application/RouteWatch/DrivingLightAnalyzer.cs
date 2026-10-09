namespace GwsBusinessSuite.Application.RouteWatch;

public enum DrivingLightKind
{
    // Low sun roughly ahead of the driver - the classic sunrise/sunset windshield glare.
    SunGlare,

    // After civil dusk / before civil dawn: headlights-on driving.
    Dark
}

// A stretch of a trip with sun glare or darkness. Path is that stretch of the route, so the globe
// can draw it over the route line; Heading is the general direction of travel along it.
public sealed record DrivingLightStretch(
    DrivingLightKind Kind,
    double StartMile,
    double EndMile,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Heading,
    IReadOnlyList<RoutePoint> Path);

// Sun glare and darkness along a planned drive, from nothing but the route and a departure time:
// the time each point is reached comes from OSRM's own per-leg durations, and the sun's position
// there from NOAA's solar position equations (accurate to well under a degree - far finer than
// "is the sun in your eyes" needs). No data source, no key, no network.
public static class DrivingLightAnalyzer
{
    // The sun is a glare problem while it's low enough to clear the visor - and only while it's up.
    public const double GlareMaxElevationDegrees = 20;
    // How far off straight ahead the sun can be and still sit in the windshield.
    public const double GlareMaxAngleOffHeadingDegrees = 25;
    // Civil twilight: below this it is dark enough for headlights.
    public const double DarkBelowElevationDegrees = -6;
    // Shorter flagged runs than this are noise from bends in the road, not something to plan for.
    private const double MinStretchMiles = 1.0;
    // A gap shorter than this between two runs of the same kind joins them into one stretch.
    private const double MergeGapMiles = 0.5;
    private const double MetersPerMile = 1609.344;

    public static IReadOnlyList<DrivingLightStretch> Analyze(
        IReadOnlyList<RoutePoint> path,
        IReadOnlyList<RouteLeg>? legs,
        double totalDurationMinutes,
        DateTimeOffset departureUtc)
    {
        if (path.Count < 2) return [];

        var miles = new double[path.Count];
        for (var i = 1; i < path.Count; i++)
        {
            miles[i] = miles[i - 1] + DistanceMeters(path[i - 1], path[i]) / MetersPerMile;
        }
        var totalMiles = miles[^1];
        if (totalMiles <= 0) return [];

        // Classify each segment by the conditions at its midpoint.
        var segments = new List<(int From, int To, DrivingLightKind? Kind, double Bearing)>();
        for (var i = 0; i + 1 < path.Count; i++)
        {
            if (miles[i + 1] - miles[i] <= 0) continue;
            var midMile = (miles[i] + miles[i + 1]) / 2;
            var at = departureUtc.AddMinutes(MinutesAtMile(midMile, legs, totalMiles, totalDurationMinutes));
            var midLat = (path[i].Latitude + path[i + 1].Latitude) / 2;
            var midLon = (path[i].Longitude + path[i + 1].Longitude) / 2;
            var bearing = Bearing(path[i], path[i + 1]);
            var (azimuth, elevation) = SunPosition(at, midLat, midLon);
            DrivingLightKind? kind =
                elevation < DarkBelowElevationDegrees ? DrivingLightKind.Dark
                : elevation is >= 0 and <= GlareMaxElevationDegrees && AngleBetween(azimuth, bearing) <= GlareMaxAngleOffHeadingDegrees ? DrivingLightKind.SunGlare
                : null;
            segments.Add((i, i + 1, kind, bearing));
        }

        // Group consecutive same-kind segments, bridging tiny gaps, then drop runs too short to matter.
        var runs = new List<(DrivingLightKind Kind, int From, int To, List<(double Bearing, double Miles)> Headings)>();
        foreach (var segment in segments)
        {
            if (segment.Kind is not { } kind) continue;
            var length = miles[segment.To] - miles[segment.From];
            if (runs.Count > 0 && runs[^1].Kind == kind && miles[segment.From] - miles[runs[^1].To] <= MergeGapMiles)
            {
                var last = runs[^1];
                last.Headings.Add((segment.Bearing, length));
                runs[^1] = (kind, last.From, segment.To, last.Headings);
            }
            else
            {
                runs.Add((kind, segment.From, segment.To, [(segment.Bearing, length)]));
            }
        }

        return runs
            .Where(run => miles[run.To] - miles[run.From] >= MinStretchMiles)
            .Select(run => new DrivingLightStretch(
                run.Kind,
                miles[run.From],
                miles[run.To],
                departureUtc.AddMinutes(MinutesAtMile(miles[run.From], legs, totalMiles, totalDurationMinutes)),
                departureUtc.AddMinutes(MinutesAtMile(miles[run.To], legs, totalMiles, totalDurationMinutes)),
                CompassName(AverageBearing(run.Headings)),
                path.Skip(run.From).Take(run.To - run.From + 1).ToList()))
            .ToList();
    }

    // Drive time to a mile marker: linear within each leg (OSRM gives a duration per leg, which
    // already accounts for that leg's road speeds); stops' dwell time isn't known, so it's zero.
    // The legs' distances are rescaled to the measured path length so both agree at the end.
    public static double MinutesAtMile(double mile, IReadOnlyList<RouteLeg>? legs, double totalMiles, double totalDurationMinutes)
    {
        if (legs is not { Count: > 0 } || legs.Sum(l => l.DistanceMiles) <= 0)
        {
            return totalMiles <= 0 ? 0 : totalDurationMinutes * Math.Clamp(mile / totalMiles, 0, 1);
        }

        var scale = totalMiles / legs.Sum(l => l.DistanceMiles);
        var minutes = 0d;
        var legStart = 0d;
        foreach (var leg in legs)
        {
            var legMiles = leg.DistanceMiles * scale;
            if (mile <= legStart + legMiles || ReferenceEquals(leg, legs[^1]))
            {
                return minutes + (legMiles <= 0 ? 0 : leg.DurationMinutes * Math.Clamp((mile - legStart) / legMiles, 0, 1));
            }
            minutes += leg.DurationMinutes;
            legStart += legMiles;
        }
        return minutes;
    }

    // NOAA's solar position algorithm (the one behind its online solar calculator). Returns the
    // sun's azimuth (degrees clockwise from true north) and elevation above the horizon (degrees,
    // negative below it), without atmospheric refraction - under a degree near the horizon.
    public static (double AzimuthDegrees, double ElevationDegrees) SunPosition(DateTimeOffset when, double latitude, double longitude)
    {
        var utc = when.UtcDateTime;
        var julianDay = utc.ToOADate() + 2415018.5;
        var t = (julianDay - 2451545.0) / 36525.0;

        var meanLongitude = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        var meanAnomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var anomalyRadians = Radians(meanAnomaly);
        var center = Math.Sin(anomalyRadians) * (1.914602 - t * (0.004817 + 0.000014 * t))
                     + Math.Sin(2 * anomalyRadians) * (0.019993 - 0.000101 * t)
                     + Math.Sin(3 * anomalyRadians) * 0.000289;
        var omega = 125.04 - 1934.136 * t;
        var apparentLongitude = meanLongitude + center - 0.00569 - 0.00478 * Math.Sin(Radians(omega));
        var meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        var obliquity = meanObliquity + 0.00256 * Math.Cos(Radians(omega));
        var declination = Math.Asin(Math.Sin(Radians(obliquity)) * Math.Sin(Radians(apparentLongitude)));

        var y = Math.Pow(Math.Tan(Radians(obliquity / 2)), 2);
        var l0 = Radians(meanLongitude);
        var equationOfTimeMinutes = 4 * Degrees(
            y * Math.Sin(2 * l0)
            - 2 * eccentricity * Math.Sin(anomalyRadians)
            + 4 * eccentricity * y * Math.Sin(anomalyRadians) * Math.Cos(2 * l0)
            - 0.5 * y * y * Math.Sin(4 * l0)
            - 1.25 * eccentricity * eccentricity * Math.Sin(2 * anomalyRadians));

        var trueSolarMinutes = Mod(utc.TimeOfDay.TotalMinutes + equationOfTimeMinutes + 4 * longitude, 1440);
        var hourAngle = Radians(trueSolarMinutes / 4 - 180);
        var latitudeRadians = Radians(latitude);

        var cosZenith = Math.Sin(latitudeRadians) * Math.Sin(declination)
                        + Math.Cos(latitudeRadians) * Math.Cos(declination) * Math.Cos(hourAngle);
        var elevation = 90 - Degrees(Math.Acos(Math.Clamp(cosZenith, -1, 1)));
        var azimuth = Mod(Degrees(Math.Atan2(
            Math.Sin(hourAngle),
            Math.Cos(hourAngle) * Math.Sin(latitudeRadians) - Math.Tan(declination) * Math.Cos(latitudeRadians))) + 180, 360);
        return (azimuth, elevation);
    }

    public static double Bearing(RoutePoint from, RoutePoint to)
    {
        var lat1 = Radians(from.Latitude);
        var lat2 = Radians(to.Latitude);
        var dLon = Radians(to.Longitude - from.Longitude);
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return Mod(Degrees(Math.Atan2(y, x)), 360);
    }

    public static string CompassName(double bearing) =>
        new[] { "NORTHBOUND", "NORTHEAST", "EASTBOUND", "SOUTHEAST", "SOUTHBOUND", "SOUTHWEST", "WESTBOUND", "NORTHWEST" }
            [(int)Math.Round(Mod(bearing, 360) / 45) % 8];

    private static double AngleBetween(double a, double b)
    {
        var difference = Math.Abs(Mod(a - b, 360));
        return difference > 180 ? 360 - difference : difference;
    }

    // Length-weighted circular mean, so a stretch's heading isn't skewed by many tiny segments.
    private static double AverageBearing(IReadOnlyList<(double Bearing, double Miles)> headings)
    {
        var x = headings.Sum(h => Math.Cos(Radians(h.Bearing)) * h.Miles);
        var y = headings.Sum(h => Math.Sin(Radians(h.Bearing)) * h.Miles);
        return Mod(Degrees(Math.Atan2(y, x)), 360);
    }

    private static double DistanceMeters(RoutePoint a, RoutePoint b)
    {
        const double earthRadiusMeters = 6371008.8;
        var dLat = Radians(b.Latitude - a.Latitude);
        var dLon = Radians(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Radians(a.Latitude)) * Math.Cos(Radians(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
    private static double Degrees(double radians) => radians * 180 / Math.PI;
    private static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
