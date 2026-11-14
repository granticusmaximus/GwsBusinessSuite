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

    // Ops with their own handler in the bridge (tiles, settings, grids), not points and paths.
    public static readonly IReadOnlySet<string> Handled = new HashSet<string>(StringComparer.Ordinal)
    {
        "setThreeD", "setNightLights", "setSatelliteScene", "clearSatelliteScene", "setMeasure", "clearMeasure",
        "setRadarTimeline", "setRadarVisible", "setTimeMachine", "clearTimeMachine", "setAurora", "setTrafficFlow",
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
        return payload;
    }

    public static IEnumerable<Dictionary<string, object?>> Items(object? payload)
    {
        if (payload is Dictionary<string, object?> item) yield return item;
        if (payload is not object?[] items) yield break;
        foreach (var value in items) if (value is Dictionary<string, object?> entry) yield return entry;
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
            "setTravelAdvice" => new("globe.americas.fill", severityTone, major),
            "setFireHotspots" => new("flame.fill", MarkerTone.Red, major),
            "setConflict" => new(kind == "conflict-confirmed" ? "target" : "scope", MarkerTone.Red, major),
            "setHumanitarian" => new("cross.fill", MarkerTone.Red, major),
            "setSatellites" => new("antenna.radiowaves.left.and.right.circle.fill", MarkerTone.Cyan),
            "setConvergence" => new("sparkles", MarkerTone.Orange, true),
            "setCoverageMarkers" => new("binoculars.fill", MarkerTone.Teal),
            "setMapNotes" => new("mappin.and.ellipse", MarkerTone.Yellow),
            "setStreetViewMarker" => new("location.fill", MarkerTone.Blue, true),
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
