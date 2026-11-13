namespace GwsBusinessSuite.Web;

// Overwatch night lights (13-11-26): NASA's VIIRS day/night band (NOAA-20) through GIBS, one night
// at a time, against the Black Marble composite (2016) as "a normal night". Dark where it's normally
// lit is how blackouts show from space. Free, no key. Clouds and moonlight also dim a night, so
// a single dark night is a lead, not proof.
public static class OverwatchNightLights
{
    public const string NightlyLayer = "VIIRS_NOAA20_DayNightBand_At_Sensor_Radiance";
    public const string BaselineLayer = "VIIRS_Black_Marble";
    public const string TileMatrixSet = "GoogleMapsCompatible_Level8";
    public const int MaxLevel = 8;
    public const int MaxNightsAgo = 30;

    public sealed record Spec(string Layer, string Time, string TileMatrixSet, int MaxLevel, string Label);

    // nightsAgo 1 is last night (the newest complete day in UTC).
    public static Spec For(int nightsAgo, bool baseline, DateTimeOffset now)
    {
        if (baseline) return new Spec(BaselineLayer, "2016-01-01", TileMatrixSet, MaxLevel, "A normal night (Black Marble 2016)");
        var nights = Math.Clamp(nightsAgo, 1, MaxNightsAgo);
        var day = now.UtcDateTime.Date.AddDays(-nights);
        return new Spec(NightlyLayer, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), TileMatrixSet, MaxLevel,
            nights == 1 ? $"Last night ({day:dd-MM-yy})" : $"{nights} nights ago ({day:dd-MM-yy})");
    }
}
