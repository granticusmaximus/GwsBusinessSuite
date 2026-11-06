using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.LiveLayers;

// US airport ground stops, delay programs, delays and closures from the FAA's National Airspace
// System status feed (key-less, about 80 major airports, 05-11-26). One pin per airport, at its
// worst: a ground stop or closure is major, a ground delay program or an hour-plus delay moderate,
// shorter delays minor. Airports are placed from airports.csv (OurAirports).
public sealed class AirportStatusService(HttpClient httpClient, IMemoryCache cache, ILogger<AirportStatusService> logger)
{
    private const string Url = "https://nasstatus.faa.gov/api/airport-status-information";
    private const string StatusPage = "https://nasstatus.faa.gov/";
    private const string CacheKey = "sky:airports";

    public async Task<IReadOnlyList<HazardFeature>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        try
        {
            var airports = Parse(await httpClient.GetStringAsync(Url, ct));
            cache.Set(CacheKey, airports, TimeSpan.FromMinutes(5));
            return airports;
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Airport status layer: FAA request failed");
            return [];
        }
    }

    public static IReadOnlyList<HazardFeature> Parse(string xml)
    {
        var doc = SafeXml(xml);
        var updated = DateTimeOffset.TryParseExact(doc.Root?.Element("Update_Time")?.Value.Replace("  ", " ", StringComparison.Ordinal).Trim(),
            "ddd MMM d HH:mm:ss yyyy 'GMT'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        var problems = new List<(string Code, int Rank, string Kind, string Text)>();
        foreach (var stop in doc.Descendants("Ground_Stop_List").Elements("Program"))
            problems.Add((Text(stop, "ARPT"), 0, "ground stop", $"Ground stop ({Reason(Text(stop, "Reason"))}) until {Text(stop, "End_Time")}"));
        foreach (var closure in doc.Descendants("Airport_Closure_List").Elements("Airport"))
            problems.Add(Closure(Text(closure, "ARPT"), Text(closure, "Reason"), Text(closure, "Reopen")));
        foreach (var program in doc.Descendants("Ground_Delay_List").Elements("Ground_Delay"))
            problems.Add((Text(program, "ARPT"), 1, "ground delay program",
                $"Ground delay program ({Reason(Text(program, "Reason"))}): average {Text(program, "Avg")}, up to {Text(program, "Max")}"));
        foreach (var delay in doc.Descendants("Arrival_Departure_Delay_List").Elements("Delay"))
        {
            var leg = delay.Element("Arrival_Departure");
            var max = leg?.Element("Max")?.Value ?? "";
            var direction = (leg?.Attribute("Type")?.Value ?? "Arrival and departure").ToLowerInvariant();
            problems.Add((Text(delay, "ARPT"), max.Contains("hour", StringComparison.OrdinalIgnoreCase) ? 1 : 2, "delays",
                $"{char.ToUpperInvariant(direction[0])}{direction[1..]} delays {leg?.Element("Min")?.Value} to {max}" +
                (leg?.Element("Trend")?.Value is { Length: > 0 } trend ? $", {trend.ToLowerInvariant()}" : "") + $" ({Reason(Text(delay, "Reason"))})"));
        }

        var features = new List<HazardFeature>();
        foreach (var airport in problems.Where(p => p.Code.Length > 0).GroupBy(p => p.Code.ToUpperInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (AircraftDetailService.AirportByCode(airport.Key) is not { } place) continue;
            var worst = airport.OrderBy(p => p.Rank).First();
            features.Add(new HazardFeature($"faa-{airport.Key}", HazardKinds.Airport, $"{airport.Key} ({place.City}): {worst.Kind}",
                string.Join(" · ", airport.OrderBy(p => p.Rank).Select(p => p.Text).Distinct()),
                place.Latitude, place.Longitude, worst.Rank switch { 0 => "major", 1 => "moderate", _ => "minor" }, StatusPage, updated));
        }
        return features;
    }

    // Most "closures" in the feed are notices to pilots (NOTAMs, starting "!"), and many only close the
    // airport to some traffic: LAX, LAS and SAN on 06-11-26 were "clsd to non sked transient ga"
    // (closed to visiting private planes without a slot), with airline flights unaffected. Those and
    // the closures at set hours each day ("clsd dly") are minor; only a real closure is major.
    private static (string Code, int Rank, string Kind, string Text) Closure(string code, string reason, string reopen)
    {
        var notam = reason.StartsWith('!');
        var (rank, kind) = !notam ? (0, "closed")
            : reason.Contains(" clsd to ", StringComparison.OrdinalIgnoreCase) ? (2, "closed to some traffic")
            : reason.Contains("clsd dly", StringComparison.OrdinalIgnoreCase) ? (2, "closed at set hours each day")
            : (0, "closed");
        var label = char.ToUpperInvariant(kind[0]) + kind[1..];
        var why = notam ? $" (NOTAM {reason.TrimStart('!').Trim().ToUpperInvariant()})" : $" ({Reason(reason)})";
        return (code, rank, kind, $"{label}{why}, reopening {reopen}");
    }

    // The FAA's reason codes, in words.
    public static string Reason(string? raw)
    {
        var reason = raw?.Trim() ?? "";
        if (reason.Length == 0) return "no reason given";
        if (reason.Contains("SWAP", StringComparison.OrdinalIgnoreCase)) return "routing around severe weather";
        if (reason.StartsWith("WX:", StringComparison.OrdinalIgnoreCase)) return reason[3..].Trim().ToLowerInvariant();
        if (reason.StartsWith("VOL", StringComparison.OrdinalIgnoreCase)) return "traffic volume";
        if (reason.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)) return "runway work or closure";
        if (reason.StartsWith("EQ", StringComparison.OrdinalIgnoreCase)) return "equipment";
        return reason.ToLowerInvariant();
    }

    private static string Text(XElement e, string name) => e.Element(name)?.Value.Trim() ?? "";

    public static XDocument SafeXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
}

// The aurora: NOAA SWPC's OVATION forecast of the chance of seeing it (30-90 minutes ahead), and
// the planetary Kp index (05-11-26, key-less). The 1° grid is cut to the two polar bands where the
// aurora can be, each Rows x Columns bytes from north to south and from 180°W eastward.
public sealed record AuroraForecast(DateTimeOffset? ForecastFor, double? Kp, DateTimeOffset? KpAt, int Peak, byte[] North, byte[] South)
{
    public const int Rows = 51;
    public const int Columns = 360;
    public const int BandEdge = 40;

    public string KpLabel => Kp is { } kp ? SpaceWeatherService.Describe(kp) : "not known";
}

public sealed class SpaceWeatherService(HttpClient httpClient, IMemoryCache cache, ILogger<SpaceWeatherService> logger)
{
    private const string OvationUrl = "https://services.swpc.noaa.gov/json/ovation_aurora_latest.json";
    private const string KpUrl = "https://services.swpc.noaa.gov/products/noaa-planetary-k-index.json";
    private const string CacheKey = "sky:aurora";

    public async Task<AuroraForecast?> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out AuroraForecast? cached) && cached is not null) return cached;
        try
        {
            var ovation = httpClient.GetStringAsync(OvationUrl, ct);
            var kp = httpClient.GetStringAsync(KpUrl, ct);
            await Task.WhenAll(ovation, kp);
            var (north, south, peak, forecastFor) = ParseOvation(ovation.Result);
            var (index, at) = ParseKp(kp.Result);
            var forecast = new AuroraForecast(forecastFor, index, at, peak, north, south);
            cache.Set(CacheKey, forecast, TimeSpan.FromMinutes(10));
            return forecast;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Aurora layer: SWPC request failed");
            return null;
        }
    }

    // OVATION's "coordinates" are [longitude 0-359, latitude -90-90, chance 0-100].
    public static (byte[] North, byte[] South, int Peak, DateTimeOffset? ForecastFor) ParseOvation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var north = new byte[AuroraForecast.Rows * AuroraForecast.Columns];
        var south = new byte[AuroraForecast.Rows * AuroraForecast.Columns];
        var peak = 0;
        foreach (var point in doc.RootElement.GetProperty("coordinates").EnumerateArray())
        {
            if (point.GetArrayLength() < 3) continue;
            var lon = (int)Math.Round(point[0].GetDouble());
            var lat = (int)Math.Round(point[1].GetDouble());
            var chance = (int)Math.Clamp(Math.Round(point[2].GetDouble()), 0, 100);
            if (chance == 0 || Math.Abs(lat) < AuroraForecast.BandEdge) continue;
            var column = ((lon % 360) + 360 + 180) % 360;
            var (band, row) = lat > 0 ? (north, 90 - lat) : (south, -AuroraForecast.BandEdge - lat);
            if (row < 0 || row >= AuroraForecast.Rows) continue;
            band[row * AuroraForecast.Columns + column] = (byte)chance;
            peak = Math.Max(peak, chance);
        }
        var forecastFor = doc.RootElement.TryGetProperty("Forecast Time", out var time) && DateTimeOffset.TryParse(time.GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        return (north, south, peak, forecastFor);
    }

    // The latest Kp. SWPC has served it as objects ({"time_tag","Kp"}) and as rows under a header row.
    public static (double? Kp, DateTimeOffset? At) ParseKp(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.EnumerateArray().Reverse())
        {
            string? time = null;
            double? kp = null;
            if (row.ValueKind == JsonValueKind.Object)
            {
                time = row.TryGetProperty("time_tag", out var t) ? t.GetString() : null;
                kp = row.TryGetProperty("Kp", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetDouble() : null;
            }
            else if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 2)
            {
                time = row[0].GetString();
                kp = double.TryParse(row[1].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            }
            if (kp is null) continue;
            return (kp, DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null);
        }
        return (null, null);
    }

    // NOAA's geomagnetic storm scale.
    public static string Describe(double kp) => kp switch
    {
        >= 9 => "G5 extreme storm",
        >= 8 => "G4 severe storm",
        >= 7 => "G3 strong storm",
        >= 6 => "G2 moderate storm",
        >= 5 => "G1 minor storm",
        >= 4 => "active",
        _ => "quiet"
    };
}
