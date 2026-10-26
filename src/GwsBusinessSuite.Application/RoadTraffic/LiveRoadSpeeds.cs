using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RoadTraffic;

// A measured speed on one stretch of road (a sensor/probe "link"), newest reading only.
public sealed record LiveRoadSpeed(string Id, string Name, string Area, double SpeedMph, int? TravelTimeSeconds,
    DateTimeOffset AsOf, IReadOnlyList<(double Longitude, double Latitude)> Path, string Source, string SourceUrl);

public sealed record LiveRoadSpeedResult(IReadOnlyList<LiveRoadSpeed> Links, IReadOnlyList<string> Sources, string? Note);

// A place that publishes live road speeds without a key. Coverage is where its links are, so a
// view elsewhere never calls it.
public interface ILiveRoadSpeedProvider
{
    string SourceName { get; }
    BoundingBox Coverage { get; }
    Task<IReadOnlyList<LiveRoadSpeed>> GetAllAsync(CancellationToken ct = default);
}

public sealed class LiveRoadSpeedService(IEnumerable<ILiveRoadSpeedProvider> providers, ILogger<LiveRoadSpeedService> logger)
{
    public async Task<LiveRoadSpeedResult> GetAsync(BoundingBox view, CancellationToken ct = default)
    {
        var covering = providers.Where(p => p.Coverage.Intersects(view)).ToList();
        if (covering.Count == 0) return new([], [], null);
        var links = new List<LiveRoadSpeed>();
        var sources = new List<string>();
        string? note = null;
        foreach (var provider in covering)
        {
            try
            {
                var all = await provider.GetAllAsync(ct);
                links.AddRange(all.Where(l => l.Path.Any(p => view.Contains(p.Latitude, p.Longitude))));
                sources.Add(provider.SourceName);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Live road speeds: {Source} failed", provider.SourceName);
                note = $"{provider.SourceName} speeds couldn't be reached just now.";
            }
        }
        return new(links, sources, note);
    }
}

// NYC DOT's real-time traffic speeds (NYC Open Data "DOT Traffic Speeds NBE", Socrata i4gi-tjb9,
// key-less): mostly the highways and bridges, a reading per link every few minutes when the
// link reports. Timestamps are New York local time with no offset.
public sealed class NycDotSpeedProvider(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<NycDotSpeedProvider> logger) : ILiveRoadSpeedProvider
{
    private const string Url = "https://data.cityofnewyork.us/resource/i4gi-tjb9.json";
    public const string AttributionUrl = "https://data.cityofnewyork.us/Transportation/DOT-Traffic-Speeds-NBE/i4gi-tjb9";
    private const string CacheKey = "livespeeds:nycdot";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(3);
    // Older readings aren't shown as "live".
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public string SourceName => "NYC DOT";
    public BoundingBox Coverage { get; } = new(40.93, 40.49, -73.68, -74.27);

    public async Task<IReadOnlyList<LiveRoadSpeed>> GetAllAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<LiveRoadSpeed>? cached) && cached is not null) return cached;
        var since = TimeZoneInfo.ConvertTime(time.GetUtcNow() - MaxAge, NewYork);
        var query = new Dictionary<string, string>
        {
            ["$select"] = "link_id,speed,travel_time,data_as_of,link_points,link_name,borough",
            // Status -101 rows are links with no valid reading (mostly 0 mph, some impossible
            // values, checked 2026-10-25); status 0 rows are real measurements.
            ["$where"] = $"data_as_of > '{since.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}' AND status = '0'",
            ["$order"] = "data_as_of DESC",
            ["$limit"] = "5000"
        };
        var url = Url + "?" + string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var links = Parse(await httpClient.GetStringAsync(url, ct));
        logger.LogDebug("NYC DOT speeds: {Count} links", links.Count);
        cache.Set(CacheKey, links, CacheFor);
        return links;
    }

    // Newest reading per link; a reading with no speed, or points that don't parse, is left out.
    public static IReadOnlyList<LiveRoadSpeed> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var newest = new Dictionary<string, LiveRoadSpeed>(StringComparer.Ordinal);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var id = Text(row, "link_id");
            if (id.Length == 0 || !double.TryParse(Text(row, "speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0 || speed > 120) continue;
            if (!DateTime.TryParse(Text(row, "data_as_of"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            var asOf = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), NewYork.GetUtcOffset(local));
            var path = ParsePoints(Text(row, "link_points"));
            if (path.Count < 2) continue;
            int? travel = int.TryParse(Text(row, "travel_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;
            var link = new LiveRoadSpeed(id, Text(row, "link_name"), Text(row, "borough"), speed, travel, asOf, path, "NYC DOT", AttributionUrl);
            if (!newest.TryGetValue(id, out var existing) || existing.AsOf < asOf) newest[id] = link;
        }
        return newest.Values.OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
    }

    // "lat,lon lat,lon ..." - the feed sometimes cuts the last pair short.
    public static IReadOnlyList<(double Longitude, double Latitude)> ParsePoints(string text)
    {
        var points = new List<(double, double)>();
        foreach (var pair in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || lat is < 40 or > 41.5 || lon is < -75 or > -73) continue;
            points.Add((lon, lat));
        }
        return points;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
