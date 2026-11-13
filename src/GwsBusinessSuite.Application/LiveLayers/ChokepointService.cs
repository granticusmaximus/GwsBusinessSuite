using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.LiveLayers;

// Chokepoints and ports (Overwatch, 13-11-26): daily ship transits through the world's 28 maritime
// chokepoints (Suez, Panama, Hormuz, Bab el-Mandeb, Bosporus, Malacca...) from IMF PortWatch's open
// data (satellite AIS, free, no key; it runs about five days behind). Each chokepoint is a pin: the
// last 7 days' average against the 30 days before, so a sudden fall in traffic stands out.
public sealed class ChokepointService(HttpClient httpClient, IMemoryCache cache, ILogger<ChokepointService> logger)
{
    private const string Base = "https://services9.arcgis.com/weJ1QsnbMYJlCHdG/arcgis/rest/services/";
    private const string Places = Base + "PortWatch_chokepoints_database/FeatureServer/0/query?where=1%3D1&outFields=portid,portname,lat,lon&returnGeometry=false&f=json";
    private const string Daily = Base + "Daily_Chokepoints_Data/FeatureServer/0/query";
    private const string Source = "https://portwatch.imf.org/";
    private const string CacheKey = "sea:chokepoints";
    public const int WeekDays = 7;
    public const int BaselineDays = 30;

    public sealed record Day(string PortId, DateOnly Date, int Total, int Tankers);

    public async Task<IReadOnlyList<HazardFeature>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        try
        {
            var places = ParsePlaces(await httpClient.GetStringAsync(Places, ct));
            var since = DateTime.UtcNow.Date.AddDays(-(WeekDays + BaselineDays + 10)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var days = new List<Day>();
            // The server pages at its own limit (1,000 rows), so step by what actually came back.
            for (var pageNumber = 0; pageNumber < 10; pageNumber++)
            {
                var url = $"{Daily}?where={Uri.EscapeDataString($"date >= '{since}'")}&outFields=portid,date,n_total,n_tanker&orderByFields=date&resultOffset={days.Count}&resultRecordCount=2000&f=json";
                var (page, more) = ParseDays(await httpClient.GetStringAsync(url, ct));
                days.AddRange(page);
                if (!more || page.Count == 0) break;
            }
            var features = Build(places, days);
            cache.Set(CacheKey, features, TimeSpan.FromHours(3));
            return features;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chokepoints (IMF PortWatch) couldn't be read");
            return [];
        }
    }

    public static IReadOnlyDictionary<string, (string Name, double Lat, double Lon)> ParsePlaces(string json)
    {
        var places = new Dictionary<string, (string, double, double)>();
        foreach (var attributes in Attributes(json))
        {
            if (Str(attributes, "portid") is { } id && Str(attributes, "portname") is { } name
                && Num(attributes, "lat") is { } lat && Num(attributes, "lon") is { } lon)
                places[id] = (name, lat, lon);
        }
        return places;
    }

    public static (IReadOnlyList<Day> Days, bool More) ParseDays(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var more = doc.RootElement.TryGetProperty("exceededTransferLimit", out var limit) && limit.ValueKind == JsonValueKind.True;
        var days = Attributes(json)
            .Select(a => Str(a, "portid") is { } id && Str(a, "date") is { } d
                && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? new Day(id, date, (int)(Num(a, "n_total") ?? 0), (int)(Num(a, "n_tanker") ?? 0)) : null)
            .OfType<Day>().ToList();
        return (days, more);
    }

    // One pin per chokepoint with data. Severity: down 40%+ on its normal is major, 20%+ moderate.
    public static IReadOnlyList<HazardFeature> Build(IReadOnlyDictionary<string, (string Name, double Lat, double Lon)> places, IEnumerable<Day> days)
    {
        var features = new List<HazardFeature>();
        foreach (var group in days.GroupBy(day => day.PortId))
        {
            if (!places.TryGetValue(group.Key, out var place)) continue;
            var ordered = group.OrderBy(day => day.Date).ToList();
            var latest = ordered[^1];
            var week = ordered.Where(day => day.Date > latest.Date.AddDays(-WeekDays)).ToList();
            var baseline = ordered.Where(day => day.Date <= latest.Date.AddDays(-WeekDays) && day.Date > latest.Date.AddDays(-WeekDays - BaselineDays)).ToList();
            var weekAverage = week.Average(day => day.Total);
            double? change = baseline.Count >= 7 && baseline.Average(day => day.Total) is var normal and > 0
                ? (weekAverage - normal) / normal * 100 : null;
            var severity = change is <= -40 ? "major" : change is <= -20 ? "moderate" : "minor";
            var detail = $"Transits {latest.Date:dd-MM-yy}: {latest.Total} ({latest.Tankers} tankers). Last 7 days: {weekAverage:0.#} a day"
                + (change is { } c ? $", {(c >= 0 ? "+" : "")}{c:0}% on the 30 days before." : ".")
                + " Source: IMF PortWatch (satellite AIS, a few days behind).";
            features.Add(new HazardFeature($"chokepoint-{group.Key}", "chokepoint", place.Name, detail, place.Lat, place.Lon, severity, Source,
                new DateTimeOffset(latest.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));
        }
        return features.OrderBy(feature => feature.Title, StringComparer.Ordinal).ToList();
    }

    private static List<JsonElement> Attributes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return [];
        return features.EnumerateArray()
            .Where(feature => feature.TryGetProperty("attributes", out _))
            .Select(feature => feature.GetProperty("attributes").Clone())
            .ToList();
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
