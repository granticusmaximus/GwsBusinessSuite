using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Hazards;

public static class HazardKinds
{
    public const string Earthquake = "earthquake";
    public const string Wildfire = "wildfire";
    public const string River = "river";
}

public sealed record HazardFeature(
    string Id,
    string Kind,
    string Title,
    string Detail,
    double Latitude,
    double Longitude,
    // "major" / "moderate" / "minor" - same scale as normalized traffic incidents.
    string Severity,
    string Url,
    DateTimeOffset? ObservedAt);

public sealed record HazardLayerResult(IReadOnlyList<HazardFeature> Features, IReadOnlyList<string> Notes);

// Key-less hazard layers for the Overwatch globe, each verified live 2026-10-08:
//   - USGS earthquakes (M2.5+, past day, worldwide GeoJSON summary feed)
//   - NIFC WFIGS current wildfire incident locations (ArcGIS, U.S.)
//   - NOAA National Water Prediction Service river gauges (U.S.), flooding gauges only
// Fire *locations* rather than perimeter polygons: ~500 current fires' polygons would be heavy to
// send on every pan, and the point carries the facts that matter (size, containment).
public sealed class HazardLayerService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<HazardLayerService> logger)
{
    private const string QuakeFeedUrl = "https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/2.5_day.geojson";
    private const string FireLocationsUrl = "https://services3.arcgis.com/T4QMspbfLg3qTGWY/arcgis/rest/services/WFIGS_Incident_Locations_Current/FeatureServer/0/query";
    private const string GaugesUrl = "https://api.water.noaa.gov/nwps/v1/gauges";
    // The gauge API returns every gauge in the box (~570 for Georgia alone, ~0.6 MB), so it is
    // only asked about views up to roughly a few states across.
    public const double MaxGaugeViewDegrees = 12;
    private static readonly BoundingBox UnitedStates = new(72, 17, -64, -180);

    public async Task<HazardLayerResult> GetHazardsAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var quakesTask = GetQuakesAsync(cancellationToken);
        var firesTask = bbox.Intersects(UnitedStates) ? GetFiresAsync(cancellationToken) : Task.FromResult<IReadOnlyList<HazardFeature>>([]);
        var gaugesTask = Task.FromResult<IReadOnlyList<HazardFeature>>([]);
        if (bbox.Intersects(UnitedStates))
        {
            if (bbox.North - bbox.South <= MaxGaugeViewDegrees && bbox.East - bbox.West <= MaxGaugeViewDegrees)
                gaugesTask = GetFloodingGaugesAsync(bbox, cancellationToken);
            else
                notes.Add("Zoom in to about state level to see flooding river gauges.");
        }
        await Task.WhenAll(quakesTask, firesTask, gaugesTask);

        var features = quakesTask.Result.Concat(firesTask.Result).Concat(gaugesTask.Result)
            .Where(f => bbox.Contains(f.Latitude, f.Longitude))
            .ToList();
        return new HazardLayerResult(features, notes);
    }

    private Task<IReadOnlyList<HazardFeature>> GetQuakesAsync(CancellationToken ct) =>
        CachedAsync("hazards:quakes", TimeSpan.FromMinutes(5), async () =>
            ParseQuakes(await httpClient.GetStringAsync(QuakeFeedUrl, ct)));

    private Task<IReadOnlyList<HazardFeature>> GetFiresAsync(CancellationToken ct) =>
        CachedAsync("hazards:fires", TimeSpan.FromMinutes(15), async () =>
        {
            // Wildfires and complexes only: about a third of "current" incidents are prescribed
            // burns (IncidentTypeCategory RX - 152 of 496 on 2026-10-08), which are planned and
            // controlled, not hazards to route around.
            var url = FireLocationsUrl + "?where=IncidentTypeCategory%20IN%20(%27WF%27%2C%27CX%27)"
                      + "&outFields=UniqueFireIdentifier,IncidentName,IncidentSize,PercentContained,FireDiscoveryDateTime,POOState"
                      + "&returnGeometry=true&outSR=4326&f=json&resultRecordCount=2000";
            return ParseFires(await httpClient.GetStringAsync(url, ct));
        });

    private Task<IReadOnlyList<HazardFeature>> GetFloodingGaugesAsync(BoundingBox bbox, CancellationToken ct)
    {
        // Rounded outward to whole degrees so small pans reuse the cached answer.
        var box = new BoundingBox(Math.Ceiling(bbox.North), Math.Floor(bbox.South), Math.Ceiling(bbox.East), Math.Floor(bbox.West));
        var key = string.Create(CultureInfo.InvariantCulture, $"hazards:gauges:{box.North}:{box.South}:{box.East}:{box.West}");
        return CachedAsync(key, TimeSpan.FromMinutes(15), async () =>
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{GaugesUrl}?bbox.xmin={box.West}&bbox.ymin={box.South}&bbox.xmax={box.East}&bbox.ymax={box.North}&srid=EPSG_4326");
            return ParseFloodingGauges(await httpClient.GetStringAsync(url, ct));
        });
    }

    private async Task<IReadOnlyList<HazardFeature>> CachedAsync(string key, TimeSpan duration, Func<Task<IReadOnlyList<HazardFeature>>> load)
    {
        if (cache.TryGetValue(key, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        try
        {
            var loaded = await load();
            cache.Set(key, loaded, duration);
            return loaded;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Hazard layer {Key} failed", key);
            return [];
        }
    }

    public static IReadOnlyList<HazardFeature> ParseQuakes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<HazardFeature>();
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var p = f.GetProperty("properties");
            var c = f.GetProperty("geometry").GetProperty("coordinates");
            if (c.GetArrayLength() < 2 || !p.TryGetProperty("mag", out var magEl) || magEl.ValueKind != JsonValueKind.Number) continue;
            var mag = magEl.GetDouble();
            var place = Str(p, "place") ?? "Unknown location";
            var depth = c.GetArrayLength() > 2 && c[2].ValueKind == JsonValueKind.Number ? c[2].GetDouble() : (double?)null;
            var time = p.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()) : (DateTimeOffset?)null;
            var tsunami = p.TryGetProperty("tsunami", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.GetInt32() == 1;
            var detail = string.Join(" · ", new[]
            {
                depth is { } d ? string.Create(CultureInfo.InvariantCulture, $"depth {d:0} km") : null,
                Str(p, "alert") is { } alert ? $"PAGER alert: {alert}" : null,
                tsunami ? "tsunami flag set (check official warnings)" : null
            }.Where(x => x is not null));
            list.Add(new(f.TryGetProperty("id", out var id) ? $"quake-{id.GetString()}" : $"quake-{list.Count}", HazardKinds.Earthquake,
                string.Create(CultureInfo.InvariantCulture, $"M{mag:0.0} - {place}"), detail,
                c[1].GetDouble(), c[0].GetDouble(),
                mag >= 6 ? "major" : mag >= 4.5 ? "moderate" : "minor",
                Str(p, "url") ?? "https://earthquake.usgs.gov", time));
        }
        return list;
    }

    public static IReadOnlyList<HazardFeature> ParseFires(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<HazardFeature>();
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object
                || !g.TryGetProperty("x", out var x) || !g.TryGetProperty("y", out var y)) continue;
            var a = f.GetProperty("attributes");
            var acres = Num(a, "IncidentSize");
            var contained = Num(a, "PercentContained");
            var name = Str(a, "IncidentName") ?? "Unnamed fire";
            var discovered = Num(a, "FireDiscoveryDateTime") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : (DateTimeOffset?)null;
            var detail = string.Join(" · ", new[]
            {
                acres is { } ac ? string.Create(CultureInfo.InvariantCulture, $"{ac:N0} acres") : "size not reported",
                contained is { } pc ? string.Create(CultureInfo.InvariantCulture, $"{pc:0}% contained") : null,
                Str(a, "POOState") is { } st ? st.Replace("US-", string.Empty, StringComparison.Ordinal) : null
            }.Where(v => v is not null));
            // A fully contained fire is no longer spreading, whatever its size.
            var severity = contained >= 100 ? "minor" : acres >= 10_000 ? "major" : acres >= 1_000 ? "moderate" : "minor";
            list.Add(new($"fire-{Str(a, "UniqueFireIdentifier") ?? name}", HazardKinds.Wildfire, $"{name} Fire", detail,
                y.GetDouble(), x.GetDouble(),
                severity,
                "https://www.nifc.gov/fire-information/nfn", discovered));
        }
        return list;
    }

    // Only gauges at or above "action" stage; "no_flooding", "not_defined", "out_of_service" and
    // stale readings are dropped.
    public static IReadOnlyList<HazardFeature> ParseFloodingGauges(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<HazardFeature>();
        if (!doc.RootElement.TryGetProperty("gauges", out var gauges)) return list;
        foreach (var g in gauges.EnumerateArray())
        {
            if (!g.TryGetProperty("status", out var status) || !status.TryGetProperty("observed", out var obs)) continue;
            var category = Str(obs, "floodCategory");
            var severity = category switch { "major" => "major", "moderate" => "moderate", "minor" or "action" => "minor", _ => null };
            if (severity is null) continue;
            var lid = Str(g, "lid") ?? "";
            var stage = Num(obs, "primary");
            var unit = Str(obs, "primaryUnit") ?? "";
            var label = category == "action" ? "at action stage" : $"{category} flooding";
            var detail = stage is { } s && s > -900 ? string.Create(CultureInfo.InvariantCulture, $"{label} · stage {s:0.##} {unit}") : label;
            var time = DateTimeOffset.TryParse(Str(obs, "validTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var vt) ? vt : (DateTimeOffset?)null;
            list.Add(new($"gauge-{lid}", HazardKinds.River, Str(g, "name") ?? lid, detail,
                Num(g, "latitude") ?? 0, Num(g, "longitude") ?? 0, severity,
                $"https://water.noaa.gov/gauges/{Uri.EscapeDataString(lid)}", time));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
