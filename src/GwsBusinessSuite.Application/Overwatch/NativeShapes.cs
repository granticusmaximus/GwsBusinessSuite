using System.Globalization;

namespace GwsBusinessSuite.Application.Overwatch;

// What the Mac app's native map (MapKit) draws from each Overwatch command (13-11-26). The page sends
// the same payloads to the web globe and to the Mac; values arrive as the Mac bridge reads them from
// WebKit: numbers as double, true/false as bool, lists as object?[], objects as Dictionary<string, object?>.
// Kept here, not in the Mac-only project, so the reading is tested against real payloads
// (NativeShapesTests). Before this, layers sent as rings, multi-part paths, flat number lists, grids
// or tiles drew nothing on the Mac: weather alerts, city traffic, aurora, live flow, the time machine;
// and power outages, satellites and convergence drew pins without their areas and lines.
public static class NativeShapes
{
    public sealed record Cell(double South, double West, double North, double East, bool Strong);
    public sealed record MarkerStyle(string Symbol, MarkerTone Tone, bool Prominent = false);
    public enum MarkerTone { Red, Orange, Yellow, Green, Teal, Cyan, Blue, Purple, Pink, Gray }

    // Holo Wireframe's palette (19-11-26), the same on the web globe: green carries information,
    // amber and red only mean worse. Every kind of thing is green and told apart by its symbol;
    // Orange and Yellow are amber (moderate), Red is red (major), Gray is dim green (context).
    public static string ToneHex(MarkerTone tone) => tone switch
    {
        MarkerTone.Red => "#ff3b3b",
        MarkerTone.Orange => "#ffb000",
        MarkerTone.Yellow => "#ffb000",
        MarkerTone.Gray => "#3f8f62",
        _ => "#7dffb0",
    };

    // The glyph is near-black on every tone except red, where white reads better.
    public static bool DarkGlyph(MarkerTone tone) => tone is not MarkerTone.Red;

    public const string GlyphHex = "#05080a";

    // Ops with their own handler in the bridge (tiles, settings, grids), not points and paths.
    public static readonly IReadOnlySet<string> Handled = new HashSet<string>(StringComparer.Ordinal)
    {
        "setThreeD", "setNightLights", "setSatelliteScene", "clearSatelliteScene", "setMeasure", "clearMeasure",
        "setRadarTimeline", "setRadarVisible", "setTimeMachine", "clearTimeMachine", "setAurora", "setTrafficFlow",
        "splitSatelliteScene",
    };

    private static readonly string[] PathKeys = ["path", "points", "positions", "polygon", "ring", "coordinates", "rings", "paths"];

    // The layer's items: the payload (after a leading container id), with city traffic's live
    // speeds (a second list) drawn with its roads.
    public static object? Payload(string operation, object?[] arguments)
    {
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        var offset = arguments.FirstOrDefault() is string ? 2 : 1;
        if (operation == "setRoadTraffic" && payload is object?[] roads && arguments.ElementAtOrDefault(offset) is object?[] speeds)
            return roads.Concat(speeds).ToArray();
        // 20-11-26: these three arrive in shapes of their own, and drew nothing on the Mac map.
        return operation switch
        {
            "setMapNotes" => MapNotes(payload, arguments.ElementAtOrDefault(offset)),
            "setRoute" => Route(arguments),
            "setStreetViewMarker" => AsDouble(arguments.ElementAtOrDefault(offset - 1), out var lat) && AsDouble(arguments.ElementAtOrDefault(offset), out var lon)
                ? new object?[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = "street-view", ["title"] = "STREET VIEW", ["lat"] = lat, ["lon"] = lon } }
                : null,
            _ => payload,
        };
    }

    // Map notes' points are [lat, lon]: a pin note is its first point; an area note is its ring
    // (as [lon, lat] for Paths) with its title pinned at the middle; a note being drawn is its
    // points and the line through them.
    private static object?[]? MapNotes(object? notes, object? draft)
    {
        static (double Lat, double Lon)[] LatLon(object? points) => points is object?[] values
            ? values.OfType<object?[]>().Where(p => p.Length == 2 && AsDouble(p[0], out _) && AsDouble(p[1], out _))
                .Select(p => { AsDouble(p[0], out var lat); AsDouble(p[1], out var lon); return (lat, lon); }).ToArray()
            : [];
        static object?[] Ring((double Lat, double Lon)[] points) => points.Select(p => (object?)new object?[] { p.Lon, p.Lat }).ToArray();
        var items = new List<object?>();
        foreach (var note in Items(notes))
        {
            var points = LatLon(note.GetValueOrDefault("points"));
            if (points.Length == 0) continue;
            var item = new Dictionary<string, object?>(note, StringComparer.Ordinal);
            item.Remove("points");
            if (note.GetValueOrDefault("kind") as string == "area" && points.Length >= 3)
            {
                item["ring"] = Ring([.. points, points[0]]);
                item["lat"] = points.Average(p => p.Lat);
                item["lon"] = points.Average(p => p.Lon);
            }
            else
            {
                item["lat"] = points[0].Lat;
                item["lon"] = points[0].Lon;
            }
            items.Add(item);
        }
        if (draft is Dictionary<string, object?> drawing && LatLon(drawing.GetValueOrDefault("points")) is { Length: > 0 } drawn)
        {
            for (var i = 0; i < drawn.Length; i++)
                items.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = $"draft:{i}", ["title"] = "", ["lat"] = drawn[i].Lat, ["lon"] = drawn[i].Lon });
            if (drawn.Length >= 2)
                items.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = "draft:line", ["path"] = Ring(drawn.Length >= 3 ? [.. drawn, drawn[0]] : drawn) });
        }
        return items.ToArray();
    }

    // setRoute(path, containerId, stops): the path is [lon, lat] pairs; stops are S, 1, 2... F.
    private static object?[]? Route(object?[] arguments)
    {
        if (arguments.FirstOrDefault() is not object?[] { Length: >= 2 } path) return null;
        var items = new List<object?> { new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = "route", ["path"] = path } };
        var stops = Items(arguments.ElementAtOrDefault(2)).ToList();
        for (var i = 0; i < stops.Count; i++)
        {
            if (!TryPoint(stops[i], out var lat, out var lon)) continue;
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = $"stop:{i}", ["title"] = i == 0 ? "S" : i == stops.Count - 1 ? "F" : i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lat"] = lat, ["lon"] = lon,
            });
        }
        return items.ToArray();
    }

    public static IEnumerable<Dictionary<string, object?>> Items(object? payload)
    {
        if (payload is Dictionary<string, object?> item) yield return item;
        if (payload is not object?[] items) yield break;
        foreach (var value in items) if (value is Dictionary<string, object?> entry) yield return entry;
    }

    // A stable MapKit identity must not include position or other changing display fields. Before
    // 14-11-26 the bridge serialized the whole payload as its key, so every aircraft/ship move was
    // a remove plus add. Most payloads carry id; the ordered fallbacks cover country shading,
    // stations and provider data that use a different natural key.
    // Crowds become counts (19-11-26, Holo Wireframe): the Mac map groups crowded pins itself, since
    // Apple's grouping crashed the app. Pins in the same grid cell (cellLon by cellLat degrees) become
    // one count item ({ id: "group:...", lat, lon, count, title, groupBounds: [south, west, north,
    // east] }); a lone pin, or one carrying a callout, stays as it is. Only past GroupAbove pins.
    public const int GroupAbove = 40;

    public static IReadOnlySet<string> Groupable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        NativeOverwatchOperations.SetCameraPins, NativeOverwatchOperations.SetWorldNews,
        NativeOverwatchOperations.SetTrafficIncidents, NativeOverwatchOperations.SetConflict,
    };

    public static IReadOnlyList<Dictionary<string, object?>> Group(IReadOnlyList<Dictionary<string, object?>> items, double cellLon, double cellLat, string noun)
    {
        if (items.Count <= GroupAbove || cellLon <= 0 || cellLat <= 0) return items;
        var output = new List<Dictionary<string, object?>>();
        var cells = new Dictionary<(long X, long Y), List<(Dictionary<string, object?> Item, double Lat, double Lon)>>();
        foreach (var item in items)
        {
            if (!TryPoint(item, out var lat, out var lon) || TextValue(item, "callout") is { Length: > 0 })
            {
                output.Add(item);
                continue;
            }
            var key = ((long)Math.Floor(lon / cellLon), (long)Math.Floor(lat / cellLat));
            if (!cells.TryGetValue(key, out var members)) cells[key] = members = [];
            members.Add((item, lat, lon));
        }
        foreach (var ((x, y), members) in cells)
        {
            if (members.Count == 1)
            {
                output.Add(members[0].Item);
                continue;
            }
            output.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = FormattableString.Invariant($"group:{x}:{y}"),
                ["lat"] = members.Average(m => m.Lat),
                ["lon"] = members.Average(m => m.Lon),
                ["count"] = members.Count,
                ["title"] = FormattableString.Invariant($"{members.Count} {noun}"),
                ["groupBounds"] = new object?[] { members.Min(m => m.Lat), members.Min(m => m.Lon), members.Max(m => m.Lat), members.Max(m => m.Lon) },
            });
        }
        return output;
    }

    public static string GroupNoun(string operation) => operation switch
    {
        NativeOverwatchOperations.SetCameraPins => "cameras",
        NativeOverwatchOperations.SetWorldNews => "news stories",
        NativeOverwatchOperations.SetTrafficIncidents => "incidents",
        NativeOverwatchOperations.SetConflict => "conflict reports",
        _ => "items",
    };

    // focusLayer's payload: { web: [...], native: ["setWorldNews", ...] } focuses those layers; null
    // (or nothing) ends the focus.
    public static IReadOnlySet<string>? FocusOperations(object?[] arguments) =>
        arguments.FirstOrDefault() is Dictionary<string, object?> spec && spec.TryGetValue("native", out var value) && value is object?[] operations
            ? operations.OfType<string>().ToHashSet(StringComparer.Ordinal)
            : null;

    public static string Identity(string operation, Dictionary<string, object?> item)
    {
        foreach (var key in new[] { "id", "eventId", "cameraId", "icao24", "mmsi", "code", "stationId" })
            if (TextValue(item, key) is { Length: > 0 } value) return operation + ":" + key + ":" + value;
        foreach (var key in new[] { "name", "title", "label", "callsign" })
            if (TextValue(item, key) is { Length: > 0 } value) return operation + ":" + key + ":" + value;
        if (TryDouble(item, out var latitude, "lat", "latitude") && TryDouble(item, out var longitude, "lon", "lng", "longitude"))
            return FormattableString.Invariant($"{operation}:coordinate:{latitude:0.######},{longitude:0.######}");
        return operation + ":anonymous";
    }

    public static bool TryPoint(Dictionary<string, object?> item, out double latitude, out double longitude)
    {
        longitude = 0;
        if (!TryDouble(item, out latitude, "lat", "latitude") || !TryDouble(item, out longitude, "lon", "lng", "longitude")) return false;
        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }

    // Every line or area an item carries:
    //   path/points/positions/polygon/ring/coordinates: [[lon,lat],...] or [{lat,lon},...];
    //   rings/paths: a list of those, or of flat lists [lon,lat,lon,lat,...];
    //   flat [lon,lat,height,...] (satellite orbits);
    //   south/west/north/east: a rectangle (convergence squares).
    // Lines crossing the date line are split there, so they don't streak across the map.
    public static IEnumerable<(double Lat, double Lon)[]> Paths(Dictionary<string, object?> item)
    {
        foreach (var key in PathKeys)
        {
            if (!item.TryGetValue(key, out var value) || value is not object?[] { Length: > 0 } values) continue;
            foreach (var path in PathsFrom(values))
                foreach (var piece in SplitAtDateLine(path))
                    if (piece.Length > 1) yield return piece;
        }
        if (TryDouble(item, out var south, "south") && TryDouble(item, out var west, "west")
            && TryDouble(item, out var north, "north") && TryDouble(item, out var east, "east") && north > south)
            yield return [(south, west), (north, west), (north, east), (south, east)];
    }

    private static IEnumerable<(double Lat, double Lon)[]> PathsFrom(object?[] values)
    {
        if (values.All(value => AsDouble(value, out _)))
        {
            yield return Flat(values);
            yield break;
        }
        var points = values.Select(Pair).ToArray();
        if (points.All(point => point.HasValue))
        {
            yield return points.Select(point => point!.Value).ToArray();
            yield break;
        }
        foreach (var nested in values.OfType<object?[]>())
            foreach (var path in PathsFrom(nested)) yield return path;
    }

    // [lon, lat, lon, lat...], or [lon, lat, height...] when every third number is far past any
    // longitude (a satellite's height in metres).
    private static (double Lat, double Lon)[] Flat(object?[] values)
    {
        var numbers = values.Select(value => AsDouble(value, out var n) ? n : double.NaN).ToArray();
        var stride = numbers.Length >= 3 && numbers.Length % 3 == 0
            && Enumerable.Range(0, numbers.Length / 3).All(i => Math.Abs(numbers[i * 3 + 2]) > 180) ? 3 : 2;
        var points = new List<(double, double)>();
        for (var i = 0; i + 1 < numbers.Length; i += stride)
        {
            var (longitude, latitude) = (numbers[i], numbers[i + 1]);
            if (latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180) points.Add((latitude, longitude));
        }
        return points.ToArray();
    }

    // Exactly [lon, lat] or [lon, lat, height], or {lat, lon}; so a flat ring isn't read as one point.
    private static (double Lat, double Lon)? Pair(object? value)
    {
        if (value is Dictionary<string, object?> item) return TryPoint(item, out var lat, out var lon) ? (lat, lon) : null;
        if (value is not object?[] { Length: 2 or 3 } pair || !AsDouble(pair[0], out var longitude) || !AsDouble(pair[1], out var latitude)) return null;
        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 ? (latitude, longitude) : null;
    }

    public static IEnumerable<(double Lat, double Lon)[]> SplitAtDateLine((double Lat, double Lon)[] path)
    {
        var start = 0;
        for (var i = 1; i < path.Length; i++)
        {
            if (Math.Abs(path[i].Lon - path[i - 1].Lon) <= 180) continue;
            yield return path[start..i];
            start = i;
        }
        yield return start == 0 ? path : path[start..];
    }

    public static bool IsArea(string operation) =>
        operation is "setPowerOutages" or "setNewsShading" or "setAurora" or "setMapNotes" or "setWeatherAlerts" or "setConvergence";

    // SF Symbols for the native Mac map. Prefer the item's actual subtype over a layer-wide generic
    // pin: hazards carry kind, incidents category, ships type, and aircraft their emergency state.
    // The bridge supplies a safe mappin fallback if a future runtime lacks one of these symbols.
    // A group of pins the Mac map shows as one (19-11-26, Grant: grouped items need a better idea of
    // what they are). MapKit's own label was the first pin's title and "+6 more"; this says what the
    // group holds and names the first few different items.
    public static (string Title, string Subtitle) ClusterLabel(string operation, IReadOnlyList<string?> memberTitles)
    {
        var noun = operation switch
        {
            "setConflict" => "conflict reports",
            "setWorldNews" => "news stories",
            "setNewsShading" => "countries in the news",
            "setCameraPins" => "cameras",
            "setHazards" => "hazards",
            "setTrafficIncidents" => "traffic incidents",
            "setAircraft" => "aircraft",
            "setShips" => "ships",
            "setWeatherWarnings" => "countries with weather warnings",
            "setTravelAdvice" => "travel advisories",
            "setFireHotspots" => "fire hotspots",
            "setHumanitarian" => "humanitarian emergencies",
            "setAirportStatus" => "airports with delays",
            "setChokepoints" => "chokepoints",
            "setConvergence" => "places where signals meet",
            "setSatellites" => "satellites",
            "setRoadWeather" => "road weather stations",
            "setMessageSigns" => "road signs",
            "setInternetOutages" => "internet outages",
            "setMapNotes" => "notes",
            _ => "pins",
        };
        var distinct = memberTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        const int Named = 3;
        var subtitle = string.Join(" · ", distinct.Take(Named).Select(t => t.Length <= 48 ? t : t[..47].TrimEnd() + "…"))
            + (distinct.Count > Named ? $" · +{distinct.Count - Named} more" : "");
        return ($"{memberTitles.Count} {noun}", subtitle);
    }

    public static MarkerStyle Marker(string operation, IReadOnlyDictionary<string, object?> item)
    {
        var kind = Text(item, "kind");
        var category = Text(item, "category");
        var type = Text(item, "type");
        var major = string.Equals(Text(item, "severity"), "major", StringComparison.OrdinalIgnoreCase);
        var moderate = string.Equals(Text(item, "severity"), "moderate", StringComparison.OrdinalIgnoreCase);
        var severityTone = major ? MarkerTone.Red : moderate ? MarkerTone.Orange : MarkerTone.Yellow;

        if (operation == "setTimeMachine")
            return category switch
            {
                "aircraft" => new("airplane", MarkerTone.Cyan),
                "ship" => new("ferry.fill", MarkerTone.Orange),
                "outage" => new("bolt.fill", MarkerTone.Pink),
                "earthquake" => new("waveform.path.ecg", MarkerTone.Red),
                "crash" => new("car.side.fill", MarkerTone.Red),
                "closure" => new("exclamationmark.octagon.fill", MarkerTone.Red),
                _ => new("clock.arrow.circlepath", MarkerTone.Gray),
            };

        return operation switch
        {
            // What the camera offers, by symbol as on the web map (19-11-26): still image, live
            // video, or found not working (CameraAvailability's mode and state on the pin).
            "setCameraPins" when Text(item, "state") == "unavailable" => new("exclamationmark.triangle.fill", MarkerTone.Gray),
            "setCameraPins" when Text(item, "mode") == "livevideo" => new("video.fill", MarkerTone.Teal),
            "setCameraPins" => new("camera.fill", MarkerTone.Green),
            "setCameraAlertHighlights" => new("camera.badge.ellipsis", MarkerTone.Red, true),
            "setAircraft" when !string.IsNullOrWhiteSpace(Text(item, "emergency")) => new("airplane.circle.fill", MarkerTone.Red, true),
            "setAircraft" => new("airplane", MarkerTone.Cyan),
            "setShips" => ShipMarker(type),
            "setWorldNews" => new("newspaper.fill", MarkerTone.Purple),
            "setTrafficIncidents" => IncidentMarker(category, severityTone, major),
            "setRoadWeather" => new("thermometer.medium", MarkerTone.Blue),
            "setMessageSigns" => new("signpost.right.fill", MarkerTone.Orange),
            "setAirQuality" => new("aqi.medium", MarkerTone.Green),
            "setPowerOutages" => new("bolt.trianglebadge.exclamationmark.fill", MarkerTone.Pink, major),
            "setInternetOutages" => new("wifi.slash", MarkerTone.Red, major),
            "setAirportStatus" => new("airplane.departure", severityTone, major),
            "setChokepoints" => new("arrow.left.arrow.right", severityTone, major),
            "setWeatherWarnings" => new("cloud.bolt.rain.fill", severityTone, major),
            "setTravelAdvice" => new("globe.americas.fill", severityTone, major),
            "setFireHotspots" => new("flame.fill", MarkerTone.Red, major),
            "setConflict" => new(kind == "conflict-confirmed" ? "target" : "scope", MarkerTone.Red, major),
            "setHumanitarian" => new("cross.fill", MarkerTone.Red, major),
            "setSatellites" => new("antenna.radiowaves.left.and.right.circle.fill", MarkerTone.Cyan),
            "setConvergence" => new("sparkles", MarkerTone.Orange, true),
            "setCoverageMarkers" => new("binoculars.fill", MarkerTone.Teal),
            "setMapNotes" => new("mappin.and.ellipse", MarkerTone.Yellow),
            "setStreetViewMarker" => new("location.fill", MarkerTone.Blue, true),
            "setPhoto" => new("camera.fill", MarkerTone.Green, true),
            "setWeatherAlerts" => new("cloud.bolt.rain.fill", MarkerTone.Red, major),
            "setHazards" => HazardMarker(kind, severityTone, major),
            _ => new("mappin", MarkerTone.Yellow),
        };
    }

    private static MarkerStyle ShipMarker(string? type) => type?.ToLowerInvariant() switch
    {
        "sailing" or "pleasure craft" => new("sailboat.fill", MarkerTone.Orange),
        "cargo" or "tanker" => new("shippingbox.fill", MarkerTone.Orange),
        "search and rescue" => new("lifepreserver.fill", MarkerTone.Red, true),
        "medical" => new("cross.case.fill", MarkerTone.Red, true),
        "military" or "law enforcement" => new("shield.lefthalf.filled", MarkerTone.Gray, true),
        _ => new("ferry.fill", MarkerTone.Orange),
    };

    private static MarkerStyle IncidentMarker(string? category, MarkerTone tone, bool prominent) => category switch
    {
        "crash" => new("car.side.fill", tone, prominent),
        "closure" => new("exclamationmark.octagon.fill", MarkerTone.Red, true),
        "roadwork" => new("figure.wave", tone, prominent),
        "event" => new("person.3.fill", tone, prominent),
        _ => new("exclamationmark.triangle.fill", tone, prominent),
    };

    private static MarkerStyle HazardMarker(string? kind, MarkerTone tone, bool prominent) => kind switch
    {
        "earthquake" => new("waveform.path.ecg", tone, prominent),
        "wildfire" or "hotspot" => new("flame.fill", tone, prominent),
        "river" or "flood" => new("water.waves", tone, prominent),
        "cyclone" => new("hurricane", tone, prominent),
        "volcano" => new("mountain.2.fill", tone, prominent),
        "drought" => new("sun.dust.fill", tone, prominent),
        "landslide" => new("figure.fall", tone, prominent),
        "dust" => new("smoke.fill", tone, prominent),
        "ice" => new("snowflake", tone, prominent),
        _ => new("exclamationmark.triangle.fill", tone, prominent),
    };

    private static string? Text(IReadOnlyDictionary<string, object?> item, string key) =>
        item.TryGetValue(key, out var value) ? value?.ToString()?.Trim().ToLowerInvariant() : null;

    private static string? TextValue(IReadOnlyDictionary<string, object?> item, string key) =>
        item.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;

    // The aurora grid as areas: each band (north and south of the edge latitude) is rows x columns of
    // percent chances, row 0 at the band's top; runs of cells along a row become one rectangle, and
    // the stronger chances (30%+) are drawn again over them.
    public static IReadOnlyList<Cell> AuroraCells(Dictionary<string, object?> aurora)
    {
        var cells = new List<Cell>();
        if (!AsDouble(aurora.GetValueOrDefault("rows"), out var rows) || !AsDouble(aurora.GetValueOrDefault("columns"), out var columns)
            || !AsDouble(aurora.GetValueOrDefault("edge"), out var edge)) return cells;
        foreach (var (key, top, bottom) in new[] { ("north", 90.0, edge - 0.5), ("south", -(edge - 0.5), -90.0) })
            cells.AddRange(AuroraCells(aurora.GetValueOrDefault(key) as string, (int)rows, (int)columns, top, bottom));
        return cells;
    }

    public static IEnumerable<Cell> AuroraCells(string? base64, int rows, int columns, double top, double bottom)
    {
        if (string.IsNullOrEmpty(base64) || rows <= 0 || columns <= 0) yield break;
        byte[] chances;
        try { chances = Convert.FromBase64String(base64); } catch (FormatException) { yield break; }
        if (chances.Length != rows * columns) yield break;
        var rowHeight = (top - bottom) / rows;
        var columnWidth = 360.0 / columns;
        foreach (var (threshold, strong) in new[] { (3, false), (30, true) })
        {
            for (var row = 0; row < rows; row++)
            {
                var north = top - row * rowHeight;
                var column = 0;
                while (column < columns)
                {
                    if (chances[row * columns + column] < threshold) { column++; continue; }
                    var start = column;
                    while (column < columns && chances[row * columns + column] >= threshold) column++;
                    yield return new Cell(north - rowHeight, -180 + start * columnWidth, north, Math.Min(180, -180 + column * columnWidth), strong);
                }
            }
        }
    }

    // The time machine's pins: its incidents, hazards and live-layer tracks, in one list.
    public static object?[] TimeMachinePins(Dictionary<string, object?> snapshot) =>
        new[] { "incidents", "earthquakes", "tracks" }.SelectMany(key => snapshot.GetValueOrDefault(key) as object?[] ?? []).ToArray();

    // How many things the Mac draws for one command: pins, lines and areas (for checks and tests).
    public static int Drawn(string operation, object?[] arguments)
    {
        var payload = Payload(operation, arguments);
        switch (operation)
        {
            case "setAurora": return payload is Dictionary<string, object?> aurora ? AuroraCells(aurora).Count : 0;
            case "setTrafficFlow": return arguments.LastOrDefault() is true ? 1 : 0;
            case "setTimeMachine":
                return payload is Dictionary<string, object?> snapshot
                    ? Items(TimeMachinePins(snapshot)).Count(item => TryPoint(item, out _, out _)) + (snapshot.GetValueOrDefault("radarTileUrl") is string ? 1 : 0)
                    : 0;
        }
        if (Handled.Contains(operation)) return payload is null or false ? 0 : 1;
        return Items(payload).Sum(item => (TryPoint(item, out _, out _) ? 1 : 0) + Paths(item).Count());
    }

    public static bool TryDouble(Dictionary<string, object?> item, out double value, params string[] keys)
    {
        foreach (var key in keys) if (item.TryGetValue(key, out var raw) && AsDouble(raw, out value)) return true;
        value = 0;
        return false;
    }

    public static bool AsDouble(object? value, out double number) =>
        double.TryParse(value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && value is not bool;
}
