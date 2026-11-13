namespace GwsBusinessSuite.Application.Overwatch;

// The sun and shadow tool (13-11-26): where the sun is for a place and moment, and the shadow it
// casts, for checking when a photo was taken. NOAA's solar position equations (accurate to about a
// tenth of a degree between 1800 and 2100), no refraction correction below the horizon.
public sealed record SunAt(DateTimeOffset At, double AzimuthDegrees, double ElevationDegrees)
{
    // Shadows point away from the sun.
    public double ShadowBearingDegrees => (AzimuthDegrees + 180) % 360;

    // Shadow length cast by an object 1 unit tall; null when the sun is down.
    public double? ShadowPerUnitHeight => ElevationDegrees <= 0 ? null : 1 / Math.Tan(ElevationDegrees * Math.PI / 180);
}

public static class SunPosition
{
    public static SunAt At(double latitude, double longitude, DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        var julianDay = utc.ToOADate() + 2415018.5;
        var t = (julianDay - 2451545.0) / 36525.0;

        var meanLongitude = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        var meanAnomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var m = Rad(meanAnomaly);
        var center = Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Math.Sin(2 * m) * (0.019993 - 0.000101 * t) + Math.Sin(3 * m) * 0.000289;
        var trueLongitude = meanLongitude + center;
        var omega = 125.04 - 1934.136 * t;
        var apparentLongitude = trueLongitude - 0.00569 - 0.00478 * Math.Sin(Rad(omega));
        var meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        var obliquity = meanObliquity + 0.00256 * Math.Cos(Rad(omega));
        var declination = Deg(Math.Asin(Math.Sin(Rad(obliquity)) * Math.Sin(Rad(apparentLongitude))));

        var y = Math.Pow(Math.Tan(Rad(obliquity / 2)), 2);
        var l0 = Rad(meanLongitude);
        var equationOfTime = 4 * Deg(y * Math.Sin(2 * l0) - 2 * eccentricity * Math.Sin(m) + 4 * eccentricity * y * Math.Sin(m) * Math.Cos(2 * l0)
            - 0.5 * y * y * Math.Sin(4 * l0) - 1.25 * eccentricity * eccentricity * Math.Sin(2 * m));

        var minutes = utc.TimeOfDay.TotalMinutes;
        var trueSolarTime = Mod(minutes + equationOfTime + 4 * longitude, 1440);
        var hourAngle = trueSolarTime / 4 < 0 ? trueSolarTime / 4 + 180 : trueSolarTime / 4 - 180;

        var lat = Rad(latitude);
        var dec = Rad(declination);
        var cosZenith = Math.Clamp(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(Rad(hourAngle)), -1, 1);
        var zenith = Math.Acos(cosZenith);
        var elevation = 90 - Deg(zenith);

        double azimuth;
        var denominator = Math.Cos(lat) * Math.Sin(zenith);
        if (Math.Abs(denominator) < 1e-9) azimuth = latitude > 0 ? 180 : 0;
        else
        {
            var cosAzimuth = Math.Clamp((Math.Sin(lat) * cosZenith - Math.Sin(dec)) / denominator, -1, 1);
            azimuth = hourAngle > 0 ? Mod(Deg(Math.Acos(cosAzimuth)) + 180, 360) : Mod(540 - Deg(Math.Acos(cosAzimuth)), 360);
        }
        return new SunAt(at, Math.Round(azimuth, 2), Math.Round(elevation, 2));
    }

    // The times on a day (every 5 minutes, sun up) when a shadow points within toleranceDegrees of
    // bearingDegrees: "this shadow points north-east, so the photo was taken around ...".
    public static IReadOnlyList<SunAt> TimesForShadow(double latitude, double longitude, DateTimeOffset day, double bearingDegrees, double toleranceDegrees = 5)
    {
        var start = new DateTimeOffset(day.Date, day.Offset);
        var found = new List<SunAt>();
        for (var minutes = 0; minutes < 24 * 60; minutes += 5)
        {
            var sun = At(latitude, longitude, start.AddMinutes(minutes));
            if (sun.ElevationDegrees <= 0) continue;
            var difference = Math.Abs(Mod(sun.ShadowBearingDegrees - bearingDegrees + 180, 360) - 180);
            if (difference <= toleranceDegrees) found.Add(sun);
        }
        return found;
    }

    public static string Compass(double degrees) =>
        new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" }[(int)Math.Round(Mod(degrees, 360) / 22.5) % 16];

    private static double Rad(double degrees) => degrees * Math.PI / 180;
    private static double Deg(double radians) => radians * 180 / Math.PI;
    private static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
