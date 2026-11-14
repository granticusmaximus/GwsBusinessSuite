using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SGPdotNET.Exception;
using SGPdotNET.Observation;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record SatellitePoint(double Latitude, double Longitude, double AltitudeKm);

public sealed record SatellitePosition(
    string Id,
    string Name,
    string Group,
    double Latitude,
    double Longitude,
    double AltitudeKm,
    double PeriodMinutes,
    IReadOnlyList<SatellitePoint> Path,
    // Objects sharing this one's orbit (modules and craft docked to a station).
    IReadOnlyList<string> With,
    // What the public can see from it (07-11-26).
    SatelliteData? Data = null);

// A satellite's public data (07-11-26). Layer, TileMatrixSet, Format, Time and MaxLevel are its
// imagery as NASA GIBS map tiles, for the globe; Snapshot is a picture of what it sees now (the area
// below it, or a geostationary satellite's whole disk); Crew is who's aboard the ISS.
public sealed record SatelliteData(string Operator, string About, string? Layer, string? TileMatrixSet, string? Format, string? Time,
    int MaxLevel, string? Snapshot, string? SnapshotCaption, IReadOnlyList<string> Crew, string Link);

public sealed record SatelliteResult(IReadOnlyList<SatellitePosition> Satellites, DateTimeOffset? ElementsFrom, string? Note);

// Satellites for Overwatch's SPACE group: CelesTrak's orbital elements, key-less, propagated here
// with SGP4 (SGP.NET). CelesTrak updates its elements about every two hours and asks not to be
// polled more often, so each group is fetched at most every RefreshEvery, and the last good copy is
// kept if a fetch fails. Positions are worked out on each refresh from the cached elements.
// Since 07-11-26 only satellites whose data the public can see are shown (Grant: "show only public
// view of data. If it has data, I want to be able to view that data"), each with its data: the ISS
// with its crew, and the weather and Earth-imaging satellites with their imagery from NASA GIBS
// and NOAA, all key-less. The brightest-objects group (mostly rocket bodies), the debris near the
// stations and China's station (no public data) are no longer drawn.
public sealed class SatelliteService(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<SatelliteService> logger)
{
    private const string ElementsUrl = "https://celestrak.org/NORAD/elements/gp.php?FORMAT=tle&GROUP=";
    private const string GibsCapabilitiesUrl = "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/1.0.0/WMTSCapabilities.xml";
    private const string GibsWmsUrl = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi";
    private const string CrewUrl = "https://corquaid.github.io/international-space-station-APIs/JSON/people-in-space.json";
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(2);
    public static readonly TimeSpan KeepLastGood = TimeSpan.FromDays(2);
    public const int PathMinutes = 92;
    public const int PathStepMinutes = 2;
    public const string Stations = "stations";
    public const string Weather = "weather";
    public const string Resource = "resource";
    public const string StationGroup = "space station";
    public const string DailyImageryGroup = "whole-Earth imagery";
    public const string LandImageryGroup = "land imagery";
    public const string GeostationaryGroup = "geostationary weather";

    public static bool IsStation(string name) =>
        name.StartsWith("ISS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CSS", StringComparison.OrdinalIgnoreCase)
        || name.Contains("TIANGONG", StringComparison.OrdinalIgnoreCase);

    private enum Snapshot { None, Below, FullDisk, Region }

    private sealed record Catalogued(string Name, string Group, string Operator, string About, string? Layer, string? Format, int MaxLevel,
        bool Daily, Snapshot Snapshot, string? SnapshotUrl, string Link)
    {
        public string? TileMatrixSet => Layer is null ? null : $"GoogleMapsCompatible_Level{MaxLevel}";
    }

    // By NORAD catalog number. GIBS layer names, tile sets and formats as its capabilities listed
    // them on 07-11-26; each satellite was in CelesTrak's stations, weather or resource group.
    private static readonly Dictionary<string, Catalogued> Catalog = new(StringComparer.Ordinal)
    {
        ["25544"] = new("ISS", StationGroup, "NASA, Roscosmos, ESA, JAXA and CSA",
            "The International Space Station, crewed, about 400 km up.", null, null, 0, false, Snapshot.None, null,
            "https://www.nasa.gov/international-space-station/"),
        ["25994"] = new("Terra", DailyImageryGroup, "NASA", "MODIS true-colour imagery of the whole Earth every day (its pass is mid-morning).",
            "MODIS_Terra_CorrectedReflectance_TrueColor", "image/jpeg", 9, true, Snapshot.Below, null, "https://terra.nasa.gov/"),
        ["27424"] = new("Aqua", DailyImageryGroup, "NASA", "MODIS true-colour imagery of the whole Earth every day (its pass is early afternoon).",
            "MODIS_Aqua_CorrectedReflectance_TrueColor", "image/jpeg", 9, true, Snapshot.Below, null, "https://aqua.nasa.gov/"),
        ["37849"] = new("Suomi NPP", DailyImageryGroup, "NASA and NOAA", "VIIRS true-colour imagery of the whole Earth every day.",
            "VIIRS_SNPP_CorrectedReflectance_TrueColor", "image/jpeg", 9, true, Snapshot.Below, null, "https://www.nesdis.noaa.gov/our-satellites/currently-flying/joint-polar-satellite-system"),
        ["43013"] = new("NOAA-20", DailyImageryGroup, "NOAA", "VIIRS true-colour imagery of the whole Earth every day.",
            "VIIRS_NOAA20_CorrectedReflectance_TrueColor", "image/jpeg", 9, true, Snapshot.Below, null, "https://www.nesdis.noaa.gov/our-satellites/currently-flying/joint-polar-satellite-system"),
        ["54234"] = new("NOAA-21", DailyImageryGroup, "NOAA", "VIIRS true-colour imagery of the whole Earth every day.",
            "VIIRS_NOAA21_CorrectedReflectance_TrueColor", "image/jpeg", 9, true, Snapshot.Below, null, "https://www.nesdis.noaa.gov/our-satellites/currently-flying/joint-polar-satellite-system"),
        ["39084"] = new("Landsat 8", LandImageryGroup, "NASA and USGS",
            "30 m scenes of the land, each place about every 16 days; in NASA's Harmonized Landsat Sentinel imagery a few days after the pass. Zoom in to see them.",
            "HLS_L30_Nadir_BRDF_Adjusted_Reflectance", "image/png", 12, true, Snapshot.None, null, "https://landsat.gsfc.nasa.gov/"),
        ["49260"] = new("Landsat 9", LandImageryGroup, "NASA and USGS",
            "30 m scenes of the land, each place about every 16 days; in NASA's Harmonized Landsat Sentinel imagery a few days after the pass. Zoom in to see them.",
            "HLS_L30_Nadir_BRDF_Adjusted_Reflectance", "image/png", 12, true, Snapshot.None, null, "https://landsat.gsfc.nasa.gov/"),
        ["40697"] = new("Sentinel-2A", LandImageryGroup, "ESA (Copernicus)",
            "Scenes of the land; in NASA's Harmonized Landsat Sentinel imagery at 30 m, a few days after the pass. Zoom in to see them.",
            "HLS_S30_Nadir_BRDF_Adjusted_Reflectance", "image/png", 12, true, Snapshot.None, null, "https://sentiwiki.copernicus.eu/web/s2-mission"),
        ["42063"] = new("Sentinel-2B", LandImageryGroup, "ESA (Copernicus)",
            "Scenes of the land; in NASA's Harmonized Landsat Sentinel imagery at 30 m, a few days after the pass. Zoom in to see them.",
            "HLS_S30_Nadir_BRDF_Adjusted_Reflectance", "image/png", 12, true, Snapshot.None, null, "https://sentiwiki.copernicus.eu/web/s2-mission"),
        ["60989"] = new("Sentinel-2C", LandImageryGroup, "ESA (Copernicus)",
            "Scenes of the land; in NASA's Harmonized Landsat Sentinel imagery at 30 m, a few days after the pass. Zoom in to see them.",
            "HLS_S30_Nadir_BRDF_Adjusted_Reflectance", "image/png", 12, true, Snapshot.None, null, "https://sentiwiki.copernicus.eu/web/s2-mission"),
        ["60133"] = new("GOES-East (GOES-19)", GeostationaryGroup, "NOAA", "The Americas and the Atlantic in colour, a new picture every 10 minutes.",
            "GOES-East_ABI_GeoColor", "image/png", 7, false, Snapshot.FullDisk, "https://cdn.star.nesdis.noaa.gov/GOES19/ABI/FD/GEOCOLOR/678x678.jpg",
            "https://www.star.nesdis.noaa.gov/GOES/fulldisk.php?sat=G19"),
        ["51850"] = new("GOES-West (GOES-18)", GeostationaryGroup, "NOAA", "The Pacific and the western Americas in colour, a new picture every 10 minutes.",
            "GOES-West_ABI_GeoColor", "image/png", 7, false, Snapshot.FullDisk, "https://cdn.star.nesdis.noaa.gov/GOES18/ABI/FD/GEOCOLOR/678x678.jpg",
            "https://www.star.nesdis.noaa.gov/GOES/fulldisk.php?sat=G18"),
        ["41836"] = new("Himawari-9", GeostationaryGroup, "Japan Meteorological Agency",
            "East Asia and the western Pacific in visible light (daylight only), a new picture every 10 minutes.",
            "Himawari_AHI_Band3_Red_Visible_1km", "image/png", 7, false, Snapshot.Region, null, "https://www.data.jma.go.jp/mscweb/en/himawari89/"),
    };

    public static bool HasPublicData(string catalogNumber) => Catalog.ContainsKey(catalogNumber);

    public async Task<SatelliteResult> GetAsync(CancellationToken ct = default)
    {
        // The five downloads are independent, so they run side by side (13-11-26): a cold start
        // waited for each in turn and the layer took 10-20 s to appear.
        var stationsTask = ElementsAsync(Stations, ct);
        var weatherTask = ElementsAsync(Weather, ct);
        var resourceTask = ElementsAsync(Resource, ct);
        var timesTask = ImageryTimesAsync(ct);
        var crewTask = CrewAsync(ct);
        await Task.WhenAll(stationsTask, weatherTask, resourceTask, timesTask, crewTask);
        var (stations, weather, resource) = (stationsTask.Result, weatherTask.Result, resourceTask.Result);
        if (stations.Elements.Count == 0 && weather.Elements.Count == 0 && resource.Elements.Count == 0)
            return new SatelliteResult([], null, "CelesTrak's orbital elements couldn't be reached just now.");
        var times = timesTask.Result;
        var crew = crewTask.Result;
        var now = time.GetUtcNow();
        // The stations group is propagated whole so the ISS keeps the modules docked to it.
        var satellites = Positions(stations.Elements, StationGroup, now.UtcDateTime, withPaths: true)
            .Concat(Positions(weather.Elements.Concat(resource.Elements).Where(e => HasPublicData(CatalogNumber(e.Line1))),
                DailyImageryGroup, now.UtcDateTime, withPaths: false))
            .Where(s => HasPublicData(s.Id))
            .DistinctBy(s => s.Id)
            .Select(s =>
            {
                var entry = Catalog[s.Id];
                return s with { Name = entry.Name, Group = entry.Group, Data = DataFor(entry, s, times, crew, now) };
            })
            .OrderBy(s => s.Group, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        return new SatelliteResult(satellites, new[] { stations.FetchedAt, weather.FetchedAt, resource.FetchedAt }.Where(t => t is not null).Min(), null);
    }

    private static string CatalogNumber(string line1) => line1.Length >= 7 ? line1[2..7].Trim() : string.Empty;

    private static SatelliteData DataFor(Catalogued entry, SatellitePosition at, IReadOnlyDictionary<string, string> times, IReadOnlyList<string> crew,
        DateTimeOffset now)
    {
        string? shown = entry.Layer is null ? null
            : entry.Daily ? DailyTime(times.GetValueOrDefault(entry.Layer), now)
            : times.GetValueOrDefault(entry.Layer) ?? "default";
        var (snapshot, caption) = entry.Snapshot switch
        {
            Snapshot.Below when shown is not null => (BelowUrl(entry.Layer!, shown, at.Latitude, at.Longitude),
                $"Below it now, as {entry.Name} saw it on {DayLabel(shown)} (NASA GIBS)"),
            Snapshot.FullDisk => (entry.SnapshotUrl, "Everything it sees, the latest picture (NOAA)"),
            Snapshot.Region when shown is not null => (RegionUrl(entry.Layer!, shown), "The latest picture of the area it watches (NASA GIBS)"),
            _ => ((string?)null, (string?)null)
        };
        return new SatelliteData(entry.Operator, entry.About, entry.Layer, entry.TileMatrixSet, entry.Format, shown, entry.MaxLevel,
            snapshot, caption, entry.Group == StationGroup ? crew : [], entry.Link);
    }

    // A daily layer's last whole day: yesterday (UTC), or GIBS's newest day when that's older
    // (Suomi NPP's stopped at 02-11-26).
    public static string DailyTime(string? newest, DateTimeOffset now)
    {
        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        return DateOnly.TryParseExact(newest, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < yesterday
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // The area about 600 by 800 km below the satellite, from GIBS's WMS (EPSG:4326, so 1.3.0's box
    // is south, west, north, east).
    public static string BelowUrl(string layer, string day, double latitude, double longitude)
    {
        var south = Math.Clamp(latitude - 5, -90, 90);
        var north = Math.Clamp(latitude + 5, -90, 90);
        var west = Math.Clamp(longitude - 7, -180, 180);
        var east = Math.Clamp(longitude + 7, -180, 180);
        return string.Create(CultureInfo.InvariantCulture,
            $"{GibsWmsUrl}?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS={layer}&STYLES=&CRS=EPSG:4326&BBOX={south:0.###},{west:0.###},{north:0.###},{east:0.###}&WIDTH=560&HEIGHT=400&FORMAT=image/jpeg&TIME={day}");
    }

    // Himawari watches about 80°E to the date line.
    private static string RegionUrl(string layer, string shown) =>
        $"{GibsWmsUrl}?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS={layer}&STYLES=&CRS=EPSG:4326&BBOX=-60,80,60,180&WIDTH=500&HEIGHT=600&FORMAT=image/png&TIME={Uri.EscapeDataString(shown)}";

    private static string DayLabel(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd-MM-yy", CultureInfo.InvariantCulture) : day;

    // GIBS's newest time for each catalogued layer, from its capabilities (about 6 MB, so read every
    // 6 hours). Without it a daily layer shows yesterday and a 10-minute one GIBS's "default".
    private async Task<IReadOnlyDictionary<string, string>> ImageryTimesAsync(CancellationToken ct)
    {
        const string key = "sky:satellites:gibs-times";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, string>? cached) && cached is not null) return cached;
        try
        {
            var times = NewestTimes(await httpClient.GetStringAsync(GibsCapabilitiesUrl, ct),
                Catalog.Values.Select(c => c.Layer).OfType<string>().Distinct());
            cache.Set(key, times, TimeSpan.FromHours(6));
            return times;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Satellites layer: NASA GIBS capabilities failed");
            var none = new Dictionary<string, string>();
            cache.Set(key, (IReadOnlyDictionary<string, string>)none, TimeSpan.FromMinutes(30));
            return none;
        }
    }

    // Each layer's <Default> time, read from inside that layer's own block.
    public static IReadOnlyDictionary<string, string> NewestTimes(string capabilities, IEnumerable<string> layers)
    {
        var times = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            var at = capabilities.IndexOf($"<ows:Identifier>{layer}</ows:Identifier>", StringComparison.Ordinal);
            if (at < 0) continue;
            var end = capabilities.IndexOf("</Layer>", at, StringComparison.Ordinal);
            var open = capabilities.IndexOf("<Default>", at, StringComparison.Ordinal);
            if (open < 0 || (end >= 0 && open > end)) continue;
            var close = capabilities.IndexOf("</Default>", open, StringComparison.Ordinal);
            if (close > open) times[layer] = capabilities[(open + "<Default>".Length)..close].Trim();
        }
        return times;
    }

    // Who is aboard the ISS, from a public list kept by an ISS enthusiast (corquaid on GitHub).
    private async Task<IReadOnlyList<string>> CrewAsync(CancellationToken ct)
    {
        const string key = "sky:satellites:iss-crew";
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null) return cached;
        try
        {
            var crew = ParseCrew(await httpClient.GetStringAsync(CrewUrl, ct));
            cache.Set(key, crew, TimeSpan.FromHours(6));
            return crew;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Satellites layer: the ISS crew list failed");
            IReadOnlyList<string> none = [];
            cache.Set(key, none, TimeSpan.FromMinutes(30));
            return none;
        }
    }

    public static IReadOnlyList<string> ParseCrew(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("people", out var people) || people.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
        return people.EnumerateArray()
            .Where(p => p.TryGetProperty("iss", out var iss) && iss.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(p => p.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() : null)
            .OfType<string>()
            .ToList();
    }

    private sealed record Cached(IReadOnlyList<(string Name, string Line1, string Line2)> Elements, DateTimeOffset? FetchedAt);

    private async Task<Cached> ElementsAsync(string group, CancellationToken ct)
    {
        var fresh = $"sky:satellites:{group}";
        var lastGood = $"sky:satellites:{group}:last-good";
        if (cache.TryGetValue(fresh, out Cached? cached) && cached is not null) return cached;
        try
        {
            var elements = ParseTle(await httpClient.GetStringAsync(ElementsUrl + group, ct));
            if (elements.Count == 0) throw new InvalidOperationException($"CelesTrak's {group} group came back empty.");
            var loaded = new Cached(elements, time.GetUtcNow());
            cache.Set(fresh, loaded, RefreshEvery);
            cache.Set(lastGood, loaded, KeepLastGood);
            return loaded;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Satellites layer: CelesTrak {Group} request failed", group);
            // Don't ask again straight away; use the last good elements for a while (they stay usable for days).
            var keep = cache.TryGetValue(lastGood, out Cached? previous) && previous is not null ? previous : new Cached([], null);
            cache.Set(fresh, keep, TimeSpan.FromMinutes(20));
            return keep;
        }
    }

    // Three-line elements: a name line, then lines 1 and 2.
    public static IReadOnlyList<(string Name, string Line1, string Line2)> ParseTle(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList();
        var elements = new List<(string, string, string)>();
        for (var i = 0; i + 2 < lines.Count; i++)
        {
            if (!lines[i + 1].StartsWith("1 ", StringComparison.Ordinal) || !lines[i + 2].StartsWith("2 ", StringComparison.Ordinal)) continue;
            elements.Add((lines[i].Trim(), lines[i + 1], lines[i + 2]));
            i += 2;
        }
        return elements;
    }

    // Where each satellite is at `at`, and (for stations) where it will be over the next orbit.
    // Objects on the very same orbit (a module docked to the ISS has the ISS's elements) are one dot.
    public static IReadOnlyList<SatellitePosition> Positions(IEnumerable<(string Name, string Line1, string Line2)> elements, string group,
        DateTime at, bool withPaths)
    {
        var positions = new List<SatellitePosition>();
        // Line 2's columns 9-63 are the orbit itself; the revolution count and checksum after them
        // differ between a station and the modules docked to it.
        foreach (var orbit in elements.GroupBy(e => e.Line2.Length >= 63 ? e.Line2.Substring(8, 55) : e.Line2))
        {
            var (name, line1, line2) = orbit.First();
            try
            {
                var satellite = new Satellite(name, line1, line2);
                var here = satellite.Predict(at).ToGeodetic();
                var path = new List<SatellitePoint>();
                if (withPaths)
                    for (var minute = 0; minute <= PathMinutes; minute += PathStepMinutes)
                    {
                        var point = satellite.Predict(at.AddMinutes(minute)).ToGeodetic();
                        path.Add(new SatellitePoint(point.Latitude.Degrees, point.Longitude.Degrees, point.Altitude));
                    }
                var revolutionsPerDay = line2.Length >= 63 && double.TryParse(line2.AsSpan(52, 11), NumberStyles.Float, CultureInfo.InvariantCulture, out var revs) ? revs : 0;
                positions.Add(new SatellitePosition(line1.Length >= 7 ? line1[2..7].Trim() : name, name, group,
                    here.Latitude.Degrees, here.Longitude.Degrees, here.Altitude,
                    revolutionsPerDay > 0 ? 1440 / revolutionsPerDay : 0, path,
                    orbit.Skip(1).Select(e => e.Name).ToList()));
            }
            catch (Exception ex) when (ex is SatellitePropagationException or DecayedException or TleException or ArgumentException or FormatException)
            {
                // A decayed or malformed entry is simply left out.
            }
        }
        return positions;
    }
}
