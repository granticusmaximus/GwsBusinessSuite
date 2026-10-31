using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RoadTraffic;

// A measured speed on one stretch of road (a sensor/probe "link"), newest reading only.
// VehiclesPerHour and PercentOfFreeFlow come from sources that measure them (Finland's stations):
// how busy the road is, and its speed against its usual free-flowing speed.
public sealed record LiveRoadSpeed(string Id, string Name, string Area, double SpeedMph, int? TravelTimeSeconds,
    DateTimeOffset AsOf, IReadOnlyList<(double Longitude, double Latitude)> Path, string Source, string SourceUrl,
    int? VehiclesPerHour = null, int? PercentOfFreeFlow = null);

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

// Finland's road traffic measurement stations (Fintraffic Digitraffic TMS, key-less, 31-10-26):
// about 500 stations on the main roads, each counting every vehicle and its speed in both
// directions. A station is a point, so it's drawn as a short stretch along its bearing; the
// slower direction is the one shown, with its vehicles per hour and its speed as a share of
// that direction's free-flowing speed (Digitraffic's own traffic-state figure). Stations come
// from one list (kept a day), readings from another (kept 3 minutes).
public sealed class DigitrafficTmsSpeedProvider(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<DigitrafficTmsSpeedProvider> logger) : ILiveRoadSpeedProvider
{
    private const string StationsUrl = "https://tie.digitraffic.fi/api/tms/v1/stations";
    private const string DataUrl = "https://tie.digitraffic.fi/api/tms/v1/stations/data";
    public const string AttributionUrl = "https://www.digitraffic.fi/en/road-traffic/";
    // Digitraffic asks every client to name itself.
    public const string UserHeader = "Digitraffic-User";
    private const string StationsKey = "livespeeds:digitraffic:stations";
    private const string ReadingsKey = "livespeeds:digitraffic:readings";
    private static readonly TimeSpan StationsFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan ReadingsFor = TimeSpan.FromMinutes(3);
    // A reading older than this isn't shown as "live" (the rolling values update every minute).
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    // Half the length of the stretch drawn for a station, in metres.
    private const double HalfLengthMetres = 350;
    private const double MphPerKmh = 0.621371;

    public sealed record Station(int Id, string Name, double Latitude, double Longitude, double? Bearing);

    public string SourceName => "Digitraffic (Finland)";
    public BoundingBox Coverage { get; } = new(70.2, 59.6, 31.7, 19.0);

    public async Task<IReadOnlyList<LiveRoadSpeed>> GetAllAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(ReadingsKey, out IReadOnlyList<LiveRoadSpeed>? cached) && cached is not null) return cached;
        if (!cache.TryGetValue(StationsKey, out IReadOnlyDictionary<int, Station>? stations) || stations is null)
        {
            stations = ParseStations(await httpClient.GetStringAsync(StationsUrl, ct));
            cache.Set(StationsKey, stations, StationsFor);
        }
        var links = ParseReadings(await httpClient.GetStringAsync(DataUrl, ct), stations, time.GetUtcNow());
        logger.LogDebug("Digitraffic speeds: {Count} stations", links.Count);
        cache.Set(ReadingsKey, links, ReadingsFor);
        return links;
    }

    // Stations that are collecting, by id (removed ones and those without a position left out).
    public static IReadOnlyDictionary<int, Station> ParseStations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var stations = new Dictionary<int, Station>();
        if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return stations;
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id)) continue;
            if (properties.TryGetProperty("collectionStatus", out var status) && status.GetString() != "GATHERING") continue;
            if (!feature.TryGetProperty("geometry", out var geometry) || !geometry.TryGetProperty("coordinates", out var coordinates)
                || coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2) continue;
            var lon = coordinates[0].GetDouble();
            var lat = coordinates[1].GetDouble();
            double? bearing = properties.TryGetProperty("bearing", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : null;
            stations[id] = new Station(id, ReadableName(properties.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""), lat, lon, bearing);
        }
        return stations;
    }

    // Each station's slower direction from its rolling 5-minute readings, when it's recent.
    public static IReadOnlyList<LiveRoadSpeed> ParseReadings(string json, IReadOnlyDictionary<int, Station> stations, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var links = new List<LiveRoadSpeed>();
        if (!doc.RootElement.TryGetProperty("stations", out var list) || list.ValueKind != JsonValueKind.Array) return links;
        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id) || !stations.TryGetValue(id, out var station)) continue;
            if (!entry.TryGetProperty("sensorValues", out var values) || values.ValueKind != JsonValueKind.Array) continue;
            var readings = new Dictionary<string, (double Value, DateTimeOffset At)>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (value.TryGetProperty("name", out var name) && value.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.Number
                    && value.TryGetProperty("measuredTime", out var measured) && DateTimeOffset.TryParse(measured.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                    readings[name.GetString() ?? ""] = (number.GetDouble(), at);
            }
            var slowest = new[] { 1, 2 }
                .Select(direction => (Direction: direction,
                    Speed: readings.TryGetValue($"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA{direction}", out var speed) ? speed : ((double, DateTimeOffset)?)null,
                    Vehicles: readings.TryGetValue($"OHITUKSET_5MIN_LIUKUVA_SUUNTA{direction}", out var vehicles) ? vehicles.Value : (double?)null,
                    Percent: readings.TryGetValue($"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA{direction}_VVAPAAS{direction}", out var percent) ? percent.Value : (double?)null))
                .Where(d => d.Speed is { } s && s.Item1 > 0 && s.Item1 < 200 && now - s.Item2 <= MaxAge)
                .OrderBy(d => d.Speed!.Value.Item1)
                .FirstOrDefault();
            if (slowest.Speed is not { } reading) continue;
            links.Add(new LiveRoadSpeed($"fi-{id}", station.Name, "Finland", Math.Round(reading.Item1 * MphPerKmh, 1), null, reading.Item2,
                Stretch(station), "Digitraffic (Finland)", AttributionUrl,
                slowest.Vehicles is { } perHour ? (int)Math.Round(perHour) : null,
                slowest.Percent is { } share and > 0 and < 300 ? (int)Math.Round(share) : null));
        }
        return links.OrderBy(link => link.Id, StringComparer.Ordinal).ToList();
    }

    // "vt1_Espoo_Hirvisuo" -> "Road 1, Espoo Hirvisuo" (vt/kt/st/mt are Finland's road classes).
    public static string ReadableName(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Traffic station";
        var road = parts[0];
        var digits = new string(road.SkipWhile(char.IsLetter).ToArray());
        var place = string.Join(' ', parts.Skip(1));
        return digits.Length > 0 && road.Length > digits.Length && digits.All(char.IsDigit)
            ? place.Length > 0 ? $"Road {digits}, {place}" : $"Road {digits}"
            : string.Join(' ', parts);
    }

    // A short stretch through the station along its bearing (north-south when it has none).
    private static IReadOnlyList<(double Longitude, double Latitude)> Stretch(Station station)
    {
        var bearing = (station.Bearing ?? 0) * Math.PI / 180;
        var dLat = HalfLengthMetres * Math.Cos(bearing) / 111_320;
        var dLon = HalfLengthMetres * Math.Sin(bearing) / (111_320 * Math.Cos(station.Latitude * Math.PI / 180));
        return [(station.Longitude - dLon, station.Latitude - dLat), (station.Longitude + dLon, station.Latitude + dLat)];
    }
}
