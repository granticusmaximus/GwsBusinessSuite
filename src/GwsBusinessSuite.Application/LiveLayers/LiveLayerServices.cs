using System.Globalization;
using System.Net;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.LiveLayers;

public sealed record Aircraft(string Icao24, string Callsign, string Country, double Latitude, double Longitude,
    double? AltitudeFeet, double? SpeedKnots, double? HeadingDegrees, double? VerticalRateFeetPerMinute);

public sealed record AircraftResult(IReadOnlyList<Aircraft> Aircraft, string? Note);

// Live aircraft for the Overwatch globe from the OpenSky Network's anonymous state-vector API
// (key-less, verified live 2026-10-12). Anonymous use is limited to about 400 credits a day, and a
// box up to 25 square degrees costs 1, so: only views that small, one shared fetch per area per
// minute (the page refreshes no faster), and a back-off once OpenSky says the day's limit is spent.
public sealed class AircraftService(HttpClient httpClient, IMemoryCache cache, ILogger<AircraftService> logger)
{
    private const string StatesUrl = "https://opensky-network.org/api/states/all";
    public const double MaxSquareDegrees = 25;
    public const int MaxAircraft = 400;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    internal const string LimitKey = "aircraft:limited";

    public async Task<AircraftResult> GetAsync(BoundingBox bounds, CancellationToken ct = default)
    {
        if ((bounds.North - bounds.South) * (bounds.East - bounds.West) > MaxSquareDegrees)
            return new AircraftResult([], "Zoom in to see aircraft (OpenSky's free feed covers about 5° × 5° at a time).");
        if (cache.TryGetValue(LimitKey, out _))
            return new AircraftResult([], "OpenSky's free daily allowance is used up; aircraft come back later.");

        // Snap to a half-degree grid so nearby views share one fetch.
        var north = Math.Ceiling(bounds.North * 2) / 2;
        var south = Math.Floor(bounds.South * 2) / 2;
        var east = Math.Ceiling(bounds.East * 2) / 2;
        var west = Math.Floor(bounds.West * 2) / 2;
        var key = string.Create(CultureInfo.InvariantCulture, $"aircraft:{north}:{south}:{east}:{west}");
        if (cache.TryGetValue(key, out IReadOnlyList<Aircraft>? cached) && cached is not null)
            return new AircraftResult(InView(cached, bounds), null);

        try
        {
            var url = string.Create(CultureInfo.InvariantCulture, $"{StatesUrl}?lamin={south}&lomin={west}&lamax={north}&lomax={east}");
            using var response = await httpClient.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                cache.Set(LimitKey, true, TimeSpan.FromMinutes(30));
                return new AircraftResult([], "OpenSky's free daily allowance is used up; aircraft come back later.");
            }
            response.EnsureSuccessStatusCode();
            var list = ParseStates(await response.Content.ReadAsStringAsync(ct));
            cache.Set(key, list, CacheFor);
            return new AircraftResult(InView(list, bounds), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Aircraft layer: OpenSky request failed");
            return new AircraftResult([], "OpenSky didn't answer just now.");
        }
    }

    private static IReadOnlyList<Aircraft> InView(IReadOnlyList<Aircraft> list, BoundingBox bounds) =>
        list.Where(a => bounds.Contains(a.Latitude, a.Longitude)).Take(MaxAircraft).ToList();

    // OpenSky state vectors: [icao24, callsign, origin_country, time_position, last_contact,
    // longitude, latitude, baro_altitude (m), on_ground, velocity (m/s), true_track (deg),
    // vertical_rate (m/s), sensors, geo_altitude (m), squawk, spi, position_source]. Aircraft
    // on the ground are left out - airports would otherwise be solid clusters.
    public static IReadOnlyList<Aircraft> ParseStates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array) return [];
        var list = new List<Aircraft>();
        foreach (var s in states.EnumerateArray())
        {
            if (s.GetArrayLength() < 12) continue;
            if (s[8].ValueKind == JsonValueKind.True) continue;
            if (Number(s[6]) is not { } lat || Number(s[5]) is not { } lon) continue;
            var altitudeMeters = Number(s[7]) ?? (s.GetArrayLength() > 13 ? Number(s[13]) : null);
            list.Add(new Aircraft(
                s[0].GetString() ?? string.Empty,
                (s[1].GetString() ?? string.Empty).Trim(),
                s[2].GetString() ?? string.Empty,
                lat, lon,
                altitudeMeters * 3.28084,
                Number(s[9]) * 1.94384,
                Number(s[10]),
                Number(s[11]) * 196.85));
        }
        return list;
    }

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

public sealed record AirQualityReading(double Latitude, double Longitude, int UsAqi, double? Pm25, double? Ozone, string Category);

public sealed record AirQualityResult(IReadOnlyList<AirQualityReading> Readings, string? Note);

// Air quality across the view from Open-Meteo's key-less air-quality API (CAMS model, verified
// live 2026-10-12): a grid of points sampled in one request, refreshed at most every 30 minutes
// (the model is hourly).
public sealed class AirQualityService(HttpClient httpClient, IMemoryCache cache, ILogger<AirQualityService> logger)
{
    private const string Url = "https://air-quality-api.open-meteo.com/v1/air-quality";
    public const int Columns = 5;
    public const int Rows = 4;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    public async Task<AirQualityResult> GetAsync(BoundingBox bounds, CancellationToken ct = default)
    {
        if (bounds.North - bounds.South > 40 || bounds.East - bounds.West > 60)
            return new AirQualityResult([], "Zoom in to a region to see air quality.");
        var points = Grid(bounds);
        var key = "air-quality:" + string.Join(';', points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Latitude:0.0},{p.Longitude:0.0}")));
        if (cache.TryGetValue(key, out IReadOnlyList<AirQualityReading>? cached) && cached is not null) return new AirQualityResult(cached, null);
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{Url}?latitude={string.Join(',', points.Select(p => p.Latitude.ToString("0.###", CultureInfo.InvariantCulture)))}&longitude={string.Join(',', points.Select(p => p.Longitude.ToString("0.###", CultureInfo.InvariantCulture)))}&current=us_aqi,pm2_5,ozone");
            var readings = Parse(await httpClient.GetStringAsync(url, ct));
            cache.Set(key, readings, CacheFor);
            return new AirQualityResult(readings, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Air quality layer: Open-Meteo request failed");
            return new AirQualityResult([], "Air quality couldn't be reached just now.");
        }
    }

    // Cell centres of an evenly spaced grid, rounded so nearby views share a cache entry.
    public static IReadOnlyList<(double Latitude, double Longitude)> Grid(BoundingBox bounds)
    {
        var points = new List<(double, double)>();
        var height = bounds.North - bounds.South;
        var width = bounds.East - bounds.West;
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                var lat = bounds.South + height * (r + 0.5) / Rows;
                var lon = bounds.West + width * (c + 0.5) / Columns;
                points.Add((Math.Round(lat, 1), Math.Round(lon, 1)));
            }
        }
        return points.Distinct().ToList();
    }

    // One location answers with an object, several with an array of them.
    public static IReadOnlyList<AirQualityReading> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var readings = new List<AirQualityReading>();
        foreach (var item in items)
        {
            if (!item.TryGetProperty("current", out var current) || !current.TryGetProperty("us_aqi", out var aqiValue) || aqiValue.ValueKind != JsonValueKind.Number) continue;
            var aqi = (int)Math.Round(aqiValue.GetDouble());
            readings.Add(new AirQualityReading(item.GetProperty("latitude").GetDouble(), item.GetProperty("longitude").GetDouble(), aqi,
                Optional(current, "pm2_5"), Optional(current, "ozone"), Category(aqi)));
        }
        return readings;
    }

    // U.S. EPA AQI bands.
    public static string Category(int aqi) => aqi switch
    {
        <= 50 => "Good",
        <= 100 => "Moderate",
        <= 150 => "Unhealthy for sensitive groups",
        <= 200 => "Unhealthy",
        <= 300 => "Very unhealthy",
        _ => "Hazardous"
    };

    private static double? Optional(JsonElement current, string name) =>
        current.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

public sealed record PowerOutageArea(string Id, string Name, string State, int CustomersOut, IReadOnlyList<string> Utilities,
    double Latitude, double Longitude, IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>> Rings);

public sealed record PowerOutageResult(IReadOnlyList<PowerOutageArea> Areas, string? Note);

// Power outages from the DOE/ORNL Outage Data Initiative Nationwide (ODIN) anonymous map API
// (key-less, verified live 2026-10-12): customers out per county (or per state when zoomed far
// out), as reported by the 60+ utilities in 26 states that publish to ODIN. Areas whose
// utilities don't report to ODIN simply show nothing, which the layer says.
public sealed class PowerOutageService(HttpClient httpClient, IMemoryCache cache, ILogger<PowerOutageService> logger)
{
    private const string Url = "https://odin.ornl.gov/odi/map";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    // Counties are only drawn for views up to about this many degrees across.
    public const double CountyScaleDegrees = 12;
    // Outlines are thinned to at most this many points per ring.
    public const int MaxRingPoints = 400;

    public async Task<PowerOutageResult> GetAsync(BoundingBox bounds, CancellationToken ct = default)
    {
        var scale = Math.Max(bounds.North - bounds.South, bounds.East - bounds.West) <= CountyScaleDegrees ? "COUNTY" : "STATE";
        var north = Math.Ceiling(bounds.North);
        var south = Math.Floor(bounds.South);
        var east = Math.Ceiling(bounds.East);
        var west = Math.Floor(bounds.West);
        var key = string.Create(CultureInfo.InvariantCulture, $"power:{scale}:{north}:{south}:{east}:{west}");
        if (cache.TryGetValue(key, out IReadOnlyList<PowerOutageArea>? cached) && cached is not null) return new PowerOutageResult(cached, null);
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture, $"{Url}?minx={west}&miny={south}&maxx={east}&maxy={north}&scale={scale}");
            var areas = Parse(await httpClient.GetStringAsync(url, ct));
            cache.Set(key, areas, CacheFor);
            return new PowerOutageResult(areas, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Power outage layer: ODIN request failed");
            return new PowerOutageResult([], "ODIN outage data couldn't be reached just now.");
        }
    }

    public static IReadOnlyList<PowerOutageArea> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var areas = new List<PowerOutageArea>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var customersOut = item.TryGetProperty("metersOut", out var outValue) && outValue.ValueKind == JsonValueKind.Number ? outValue.GetInt32() : 0;
            if (customersOut <= 0 || !item.TryGetProperty("feature", out var feature) || feature.ValueKind != JsonValueKind.Object) continue;
            var rings = OuterRings(feature);
            if (rings.Count == 0) continue;
            var state = Text(item, "stateName");
            var county = Text(item, "countyName");
            var utilities = item.TryGetProperty("outages", out var outages) && outages.ValueKind == JsonValueKind.Array
                ? outages.EnumerateArray().Select(o => Text(o, "utilityName")).Where(n => n.Length > 0).Distinct().ToList()
                : [];
            var all = rings.SelectMany(r => r).ToList();
            areas.Add(new PowerOutageArea(Text(item, "id"), county.Length > 0 ? $"{county} County" : state, state, customersOut, utilities,
                (all.Min(p => p.Latitude) + all.Max(p => p.Latitude)) / 2, (all.Min(p => p.Longitude) + all.Max(p => p.Longitude)) / 2, rings));
        }
        return areas.OrderByDescending(a => a.CustomersOut).ToList();
    }

    // GeoJSON Polygon / MultiPolygon: the outer ring of each polygon, thinned for drawing.
    private static List<IReadOnlyList<(double Longitude, double Latitude)>> OuterRings(JsonElement feature)
    {
        var type = Text(feature, "type");
        if (!feature.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array) return [];
        var polygons = type == "MultiPolygon" ? coordinates.EnumerateArray().ToList() : type == "Polygon" ? [coordinates] : [];
        var rings = new List<IReadOnlyList<(double, double)>>();
        foreach (var polygon in polygons)
        {
            if (polygon.GetArrayLength() == 0) continue;
            var points = polygon[0].EnumerateArray()
                .Where(p => p.GetArrayLength() >= 2)
                .Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
            if (points.Count < 3) continue;
            var step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)MaxRingPoints));
            rings.Add(points.Where((_, i) => i % step == 0).ToList());
        }
        return rings;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
