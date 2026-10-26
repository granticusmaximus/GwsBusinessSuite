using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RoadTraffic;

// One section of road from a state's HPMS submission: how many vehicles use it on an average day
// (AADT) and what the road is like. Paths are (longitude, latitude) lines.
public sealed record RoadSegment(
    long Id, string Label, string? Name, bool Named, string RoadClass, int FunctionalClass, int VehiclesPerDay,
    int? TrucksPerDay, int? FutureVehiclesPerDay, double? PeakHourPercent, double? PeakDirectionPercent,
    int? Lanes, int? SpeedLimitMph, int? Signals, int? StopSigns, bool Toll, bool Ramp, bool OneWay,
    string State, int ReleaseYear, IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>> Paths);

public sealed record RoadTrafficResult(IReadOnlyList<RoadSegment> Roads, string? Note, string? Coverage, IReadOnlyList<string> Releases);

// Which roads a view of this size gets: wider views keep only the bigger roads, so a city-wide
// view isn't thousands of side streets. TileDegrees is the cache tile for that level.
public sealed record RoadTrafficZoom(int MaxFunctionalClass, double TileDegrees, string Coverage);

// Road traffic volumes for the Overwatch globe from FHWA's Highway Performance Monitoring System:
// each state's yearly submission is a public, key-less ArcGIS FeatureServer on geo.dot.gov
// (Hosted/HPMS_FULL_{ST}_{YEAR}, all 52 at a 2025 release when checked 2026-10-25). The data only
// changes once a year, so the state list is read once a day and each tile is kept for days.
public sealed class HpmsRoadTrafficService(HttpClient httpClient, IMemoryCache cache, ILogger<HpmsRoadTrafficService> logger)
{
    private const string Root = "https://geo.dot.gov/server/rest/services/";
    public const double MaxViewKm = 60;
    // The busiest sections are kept when a view has more than this many (the globe draws fewer).
    public const int MaxRoads = 12000;
    private const int PageSize = 2000;
    private const int MaxPages = 4;
    private const string CatalogKey = "hpms:catalog";
    private static readonly TimeSpan CatalogCacheFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan CatalogRetryAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TileCacheFor = TimeSpan.FromDays(3);
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    // geo.dot.gov is shared and not fast (~5 s for a dense tile); a few requests at a time.
    private static readonly SemaphoreSlim RequestGate = new(6, 6);

    private static readonly Regex ServiceName = new(@"^Hosted/HPMS_FULL_([A-Z]{2})\d*_(\d{4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Requested when the layer has them (older releases have a few more, newer ones fewer).
    private static readonly string[] WantedFields =
    [
        "route_signing", "route_number", "route_qualifier", "route_name", "f_system", "facility_type", "aadt",
        "aadt_single_unit", "aadt_combination", "k_factor", "dir_factor", "future_aadt", "through_lanes",
        "speed_limit", "number_signals", "stop_signs", "toll_id"
    ];

    public static RoadTrafficZoom? ZoomFor(BoundingBox view)
    {
        var km = ViewWidthKm(view);
        if (km > MaxViewKm) return null;
        if (km > 25) return new(3, 0.25, "freeways and principal arterials");
        if (km > 8) return new(5, 0.1, "freeways, arterials and major collectors");
        return new(7, 0.05, "every road with a count");
    }

    public static double ViewWidthKm(BoundingBox view)
    {
        var midLatitude = (view.North + view.South) / 2 * Math.PI / 180;
        var northSouthKm = (view.North - view.South) * 111.32;
        var eastWestKm = (view.East - view.West) * 111.32 * Math.Cos(midLatitude);
        return Math.Max(northSouthKm, eastWestKm);
    }

    public async Task<RoadTrafficResult> GetAsync(BoundingBox view, CancellationToken ct = default)
    {
        var zoom = ZoomFor(view);
        if (zoom is null)
            return new([], $"Zoom in to a city (under about {MaxViewKm:0} km across) for road traffic counts.", null, []);

        var catalog = await GetCatalogAsync(ct);
        if (catalog is null) return new([], "Road traffic counts (FHWA HPMS) couldn't be reached just now.", zoom.Coverage, []);
        var layers = catalog.Where(l => l.Extent.Intersects(view)).ToList();
        if (layers.Count == 0) return new([], "Road traffic counts cover the US (FHWA HPMS); there are none for this area.", zoom.Coverage, []);

        var requests = layers.SelectMany(layer => TilesFor(view, zoom.TileDegrees).Select(tile => GetTileAsync(layer, zoom, tile, ct))).ToList();
        var tiles = await Task.WhenAll(requests);
        if (tiles.All(t => t is null)) return new([], "Road traffic counts (FHWA HPMS) couldn't be reached just now.", zoom.Coverage, []);

        var roads = tiles.Where(t => t is not null).SelectMany(t => t!)
            .Where(r => r.Paths.Any(path => path.Any(p => view.Contains(p.Latitude, p.Longitude))) || Crosses(r, view))
            .DistinctBy(r => (r.State, r.Id))
            .OrderByDescending(r => r.VehiclesPerDay)
            .Take(MaxRoads)
            // Quietest first, so the busiest roads draw on top.
            .OrderBy(r => r.VehiclesPerDay)
            .ToList();
        var releases = layers.Select(l => $"{l.State} {l.Year}").Order(StringComparer.Ordinal).ToList();
        var note = roads.Count == 0 ? "No counted roads in this view." : tiles.Any(t => t is null) ? "Some road counts couldn't be loaded just now." : null;
        return new(roads, note, zoom.Coverage, releases);
    }

    // A long section can pass through the view with no point inside it (e.g. a straight freeway).
    private static bool Crosses(RoadSegment road, BoundingBox view) => road.Paths.Any(path =>
        path.Count >= 2 && new BoundingBox(path.Max(p => p.Latitude), path.Min(p => p.Latitude), path.Max(p => p.Longitude), path.Min(p => p.Longitude)).Intersects(view));

    public static IEnumerable<BoundingBox> TilesFor(BoundingBox view, double size)
    {
        var southIndex = (long)Math.Floor(view.South / size);
        var northIndex = (long)Math.Floor(view.North / size);
        var westIndex = (long)Math.Floor(view.West / size);
        var eastIndex = (long)Math.Floor(view.East / size);
        for (var lat = southIndex; lat <= northIndex; lat++)
        for (var lon = westIndex; lon <= eastIndex; lon++)
            yield return new BoundingBox(Math.Round((lat + 1) * size, 6), Math.Round(lat * size, 6), Math.Round((lon + 1) * size, 6), Math.Round(lon * size, 6));
    }

    private sealed record HpmsLayer(string State, int Year, string QueryUrl, string ObjectIdField, IReadOnlyDictionary<string, string> Fields, BoundingBox Extent);

    private async Task<IReadOnlyList<HpmsLayer>?> GetCatalogAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CatalogKey, out IReadOnlyList<HpmsLayer>? cached)) return cached;
        await CatalogGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CatalogKey, out cached)) return cached;
            IReadOnlyList<HpmsLayer>? catalog = null;
            try
            {
                var services = LatestServices(await httpClient.GetStringAsync(Root + "Hosted?f=json", ct));
                var loaded = await Task.WhenAll(services.Select(s => LoadLayerAsync(s.State, s.Years, ct)));
                var layers = loaded.Where(l => l is not null).Select(l => l!).ToList();
                if (layers.Count > 0) catalog = layers;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Road traffic: HPMS service list couldn't be read");
            }
            // A failed list is remembered briefly too, so every view change doesn't retry it.
            cache.Set(CatalogKey, catalog, catalog is null ? CatalogRetryAfter : CatalogCacheFor);
            return catalog;
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    // The newest releases first for each state (two kept, in case the newest is unreadable).
    public static IReadOnlyList<(string State, IReadOnlyList<int> Years)> LatestServices(string catalogJson)
    {
        using var doc = JsonDocument.Parse(catalogJson);
        if (!doc.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array) return [];
        var byState = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var service in services.EnumerateArray())
        {
            if (Text(service, "type") != "FeatureServer") continue;
            var match = ServiceName.Match(Text(service, "name"));
            if (!match.Success) continue;
            var state = match.Groups[1].Value.ToUpperInvariant();
            if (!byState.TryGetValue(state, out var years)) byState[state] = years = [];
            years.Add(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return byState.OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => (s.Key, (IReadOnlyList<int>)s.Value.Reverse().Take(2).ToList()))
            .ToList();
    }

    private async Task<HpmsLayer?> LoadLayerAsync(string state, IReadOnlyList<int> years, CancellationToken ct)
    {
        foreach (var year in years)
        {
            // Georgia's 2023 release is HPMS_FULL_GA3_2023; every other one so far has no digit.
            foreach (var service in new[] { $"HPMS_FULL_{state}_{year}", $"HPMS_FULL_{state}3_{year}" })
            {
                var layerUrl = $"{Root}Hosted/{service}/FeatureServer/0";
                try
                {
                    await RequestGate.WaitAsync(ct);
                    string json;
                    try { json = await httpClient.GetStringAsync(layerUrl + "?f=json", ct); }
                    finally { RequestGate.Release(); }
                    var layer = ParseLayer(json, state, year, layerUrl + "/query");
                    if (layer is not null) return layer;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "Road traffic: HPMS layer {Service} couldn't be read", service);
                }
            }
        }
        return null;
    }

    private static HpmsLayer? ParseLayer(string json, string state, int year, string queryUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _) || !root.TryGetProperty("fields", out var fieldList) || fieldList.ValueKind != JsonValueKind.Array) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fieldList.EnumerateArray())
        {
            var name = Text(field, "name");
            if (name.Length > 0) fields.TryAdd(name, name);
        }
        if (!fields.ContainsKey("aadt") || !fields.ContainsKey("f_system")) return null;
        var extent = root.TryGetProperty("extent", out var e) ? ParseExtent(e) : null;
        if (extent is null) return null;
        var objectId = Text(root, "objectIdField");
        return new HpmsLayer(state, year, queryUrl, objectId.Length > 0 ? objectId : "objectid", fields, extent);
    }

    // Layer extents are WGS 84 on geo.dot.gov; Web Mercator is converted in case one isn't.
    private static BoundingBox? ParseExtent(JsonElement extent)
    {
        if (extent.ValueKind != JsonValueKind.Object) return null;
        double? Read(string name) => extent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        if (Read("xmin") is not { } xmin || Read("ymin") is not { } ymin || Read("xmax") is not { } xmax || Read("ymax") is not { } ymax) return null;
        var wkid = extent.TryGetProperty("spatialReference", out var sr)
            ? (sr.TryGetProperty("latestWkid", out var latest) && latest.ValueKind == JsonValueKind.Number ? latest.GetInt32()
                : sr.TryGetProperty("wkid", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 4326)
            : 4326;
        if (wkid is 3857 or 102100)
        {
            static double Lon(double x) => x / 6378137.0 * 180 / Math.PI;
            static double Lat(double y) => (2 * Math.Atan(Math.Exp(y / 6378137.0)) - Math.PI / 2) * 180 / Math.PI;
            return new BoundingBox(Lat(ymax), Lat(ymin), Lon(xmax), Lon(xmin));
        }
        return wkid == 4326 ? new BoundingBox(ymax, ymin, xmax, xmin) : null;
    }

    private async Task<IReadOnlyList<RoadSegment>?> GetTileAsync(HpmsLayer layer, RoadTrafficZoom zoom, BoundingBox tile, CancellationToken ct)
    {
        if (!layer.Extent.Intersects(tile)) return [];
        var key = string.Create(CultureInfo.InvariantCulture, $"hpms:{layer.State}:{layer.Year}:{zoom.MaxFunctionalClass}:{tile.South}:{tile.West}");
        if (cache.TryGetValue(key, out IReadOnlyList<RoadSegment>? cached) && cached is not null) return cached;
        try
        {
            var roads = new List<RoadSegment>();
            for (var page = 0; page < MaxPages; page++)
            {
                await RequestGate.WaitAsync(ct);
                string json;
                try { json = await httpClient.GetStringAsync(QueryUrl(layer, zoom, tile, page * PageSize), ct); }
                finally { RequestGate.Release(); }
                var (segments, more) = ParseFeatures(json, layer.State, layer.Year, layer.ObjectIdField);
                roads.AddRange(segments);
                if (!more) break;
            }
            cache.Set(key, (IReadOnlyList<RoadSegment>)roads, TileCacheFor);
            return roads;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Road traffic: HPMS tile {State} {Year} {Tile} failed", layer.State, layer.Year, key);
            return null;
        }
    }

    private static string QueryUrl(HpmsLayer layer, RoadTrafficZoom zoom, BoundingBox tile, int offset)
    {
        var fields = WantedFields.Where(layer.Fields.ContainsKey).Select(f => layer.Fields[f]).Append(layer.ObjectIdField).Distinct();
        var query = new Dictionary<string, string>
        {
            ["where"] = $"{layer.Fields["aadt"]} > 0 AND {layer.Fields["f_system"]} <= {zoom.MaxFunctionalClass}",
            ["geometry"] = string.Create(CultureInfo.InvariantCulture, $"{tile.West},{tile.South},{tile.East},{tile.North}"),
            ["geometryType"] = "esriGeometryEnvelope",
            ["inSR"] = "4326",
            ["spatialRel"] = "esriSpatialRelIntersects",
            ["outFields"] = string.Join(',', fields),
            ["outSR"] = "4326",
            ["geometryPrecision"] = "5",
            // Thinned to about a pixel at that zoom level.
            ["maxAllowableOffset"] = (zoom.TileDegrees / 500).ToString("0.######", CultureInfo.InvariantCulture),
            ["orderByFields"] = layer.ObjectIdField,
            ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["resultRecordCount"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["f"] = "json"
        };
        return layer.QueryUrl + "?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    public static (IReadOnlyList<RoadSegment> Roads, bool More) ParseFeatures(string json, string state, int year, string objectIdField = "objectid")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new HttpRequestException($"HPMS query error: {Text(error, "message")}");
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return ([], false);
        var more = root.TryGetProperty("exceededTransferLimit", out var exceeded) && exceeded.ValueKind == JsonValueKind.True;
        var roads = new List<RoadSegment>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object) continue;
            var a = attributes.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            if (Int(a, "aadt") is not { } aadt || aadt <= 0) continue;
            var paths = Paths(feature);
            if (paths.Count == 0) continue;
            // The object id ties a section seen in two tiles together; without one, its first
            // point and count stand in (stable for the life of the process, which is the cache's).
            var id = a.TryGetValue(objectIdField, out var oid) && oid.ValueKind == JsonValueKind.Number ? oid.GetInt64()
                : HashCode.Combine(paths[0][0].Longitude, paths[0][0].Latitude, aadt);
            var functionalClass = Int(a, "f_system") ?? 0;
            var facility = Int(a, "facility_type");
            var name = TitleName(RawText(a, "route_name"));
            var (label, named) = RouteLabel(Int(a, "route_signing"), RawText(a, "route_number"), Int(a, "route_qualifier"), functionalClass, facility, name);
            var single = Int(a, "aadt_single_unit");
            var combination = Int(a, "aadt_combination");
            var speedLimit = Int(a, "speed_limit");
            var lanes = Int(a, "through_lanes");
            roads.Add(new RoadSegment(
                id, label, name, named, ClassName(functionalClass), functionalClass, aadt,
                single is null && combination is null ? null : (single ?? 0) + (combination ?? 0),
                Int(a, "future_aadt") is > 0 and var future ? future : null,
                Percent(a, "k_factor"), Percent(a, "dir_factor"),
                lanes is > 0 ? lanes : null,
                // 999 and similar codes mean "no posted limit".
                speedLimit is > 0 and <= 90 ? speedLimit : null,
                Int(a, "number_signals"), Int(a, "stop_signs"),
                RawText(a, "toll_id") is { Length: > 0 } toll && toll != "0",
                facility == 4, facility == 1, state, year, paths));
        }
        return (roads, more);
    }

    // Route signing (HPMS field manual): 2 Interstate, 3 U.S., 4 State, 5 off-Interstate business
    // marker, 6 County. Unsigned roads have only an internal number, so they go by their name
    // when the state sent one (NY's parkways, many city streets) and otherwise by their class.
    public static (string Label, bool Named) RouteLabel(int? signing, string? number, int? qualifier, int functionalClass, int? facilityType, string? name = null)
    {
        var ramp = facilityType == 4 ? " ramp" : string.Empty;
        number = number?.Trim();
        var unsigned = string.IsNullOrEmpty(name) ? (ClassName(functionalClass) + ramp, false) : (name + ramp, true);
        if (string.IsNullOrEmpty(number)) return unsigned;
        var label = signing switch
        {
            2 => $"I-{number}",
            3 => $"US {number}",
            4 => $"SR {number}",
            5 => $"I-{number} Business",
            6 => $"CR {number}",
            _ => null
        };
        if (label is null) return unsigned;
        var suffix = signing == 5 ? string.Empty : qualifier switch
        {
            2 => " Alt",
            3 => " Business",
            4 => " Bypass",
            5 => " Spur",
            6 => " Loop",
            9 => " Truck",
            _ => string.Empty
        };
        return (label + suffix + ramp, true);
    }

    // States send names in capitals ("GRAND CNTRL PKWY") or as written; both end up "Grand Cntrl Pkwy".
    public static string? TitleName(string? name)
    {
        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    public static string ClassName(int functionalClass) => functionalClass switch
    {
        1 => "Interstate",
        2 => "Freeway or expressway",
        3 => "Principal arterial",
        4 => "Minor arterial",
        5 => "Major collector",
        6 => "Minor collector",
        7 => "Local road",
        _ => "Road"
    };

    private static List<IReadOnlyList<(double Longitude, double Latitude)>> Paths(JsonElement feature)
    {
        var result = new List<IReadOnlyList<(double, double)>>();
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object
            || !geometry.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array) return result;
        foreach (var path in paths.EnumerateArray())
        {
            var points = path.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 && p[0].ValueKind == JsonValueKind.Number && p[1].ValueKind == JsonValueKind.Number)
                .Select(p => (p[0].GetDouble(), p[1].GetDouble()))
                .ToList();
            if (points.Count >= 2) result.Add(points);
        }
        return result;
    }

    private static int? Int(IReadOnlyDictionary<string, JsonElement> a, string name) =>
        a.TryGetValue(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => (int)Math.Round(v.GetDouble()),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (int)Math.Round(d),
            _ => null
        } : null;

    // K and D factors are percents (9 = 9 %); a state that sends fractions gets them scaled.
    private static double? Percent(IReadOnlyDictionary<string, JsonElement> a, string name)
    {
        if (!a.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        var value = v.GetDouble();
        if (value <= 0) return null;
        return value <= 1 ? value * 100 : value;
    }

    private static string? RawText(IReadOnlyDictionary<string, JsonElement> a, string name) =>
        a.TryGetValue(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        } : null;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
