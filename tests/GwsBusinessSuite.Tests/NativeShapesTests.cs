using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

// The Mac app's native map draws what NativeShapes reads. These payloads have the shapes
// OverwatchGrid sends (values as the Mac bridge gets them from WebKit). Before 13-11-26 the alerts,
// city traffic, aurora, live flow and time machine drew nothing on the Mac, and power outages,
// satellites and convergence lost their areas and lines.
public sealed class NativeShapesTests
{
    private static Dictionary<string, object?> O(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private static object?[] L(params object?[] values) => values;

    [Fact]
    public void WeatherAlerts_ShouldDrawEachRingAsAnArea()
    {
        var alert = O(("id", "a1"), ("eventName", "Flood Warning"),
            ("rings", L(L(L(-84.5, 33.6), L(-84.2, 33.6), L(-84.2, 33.9), L(-84.5, 33.9)), L(L(-85.0, 34.0), L(-84.9, 34.0), L(-84.9, 34.1)))));

        Assert.Equal(2, NativeShapes.Paths(alert).Count());
        Assert.True(NativeShapes.IsArea("setWeatherAlerts"));
        Assert.Equal(2, NativeShapes.Drawn("setWeatherAlerts", [L(alert)]));
    }

    [Fact]
    public void CityTraffic_ShouldDrawEveryRoadPiece_AndTheLiveSpeedsSentAlongside()
    {
        var road = O(("id", "GA1"), ("paths", L(L(L(-84.39, 33.75), L(-84.38, 33.76)), L(L(-84.37, 33.77), L(-84.36, 33.78), L(-84.35, 33.79)))));
        var speed = O(("id", "s1"), ("path", L(L(-84.40, 33.70), L(-84.41, 33.71))));

        Assert.Equal(3, NativeShapes.Drawn("setRoadTraffic", ["tg-viewport", L(road), L(speed), O(("liveLookup", false))]));
    }

    [Fact]
    public void PowerOutages_ShouldDrawTheirFlatRingsAsAreas_NotAsOnePointEach()
    {
        var area = O(("name", "FULTON"), ("customersOut", 1200.0), ("lat", 33.8), ("lon", -84.4),
            ("rings", L(L(-84.6, 33.6, -84.2, 33.6, -84.2, 34.0, -84.6, 34.0))));

        var path = Assert.Single(NativeShapes.Paths(area));
        Assert.Equal(4, path.Length);
        Assert.Equal((33.6, -84.6), path[0]);
        Assert.Equal(2, NativeShapes.Drawn("setPowerOutages", ["tg-viewport", L(area)]));
    }

    [Fact]
    public void Satellites_ShouldDrawTheirOrbit_FromLonLatHeight_SplitAtTheDateLine()
    {
        var iss = O(("id", "25544"), ("name", "ISS"), ("lat", 10.0), ("lon", 170.0),
            ("path", L(170.0, 10.0, 420000.0, 179.0, 11.0, 420000.0, -179.0, 12.0, 420000.0, -170.0, 13.0, 420000.0)));

        var pieces = NativeShapes.Paths(iss).ToList();

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Equal(2, piece.Length));
        Assert.Equal(3, NativeShapes.Drawn("setSatellites", [L(iss)]));
    }

    [Fact]
    public void Convergence_ShouldDrawItsSquare_AndPin()
    {
        var cell = O(("south", 33.0), ("west", -85.0), ("north", 34.0), ("east", -84.0), ("lat", 33.5), ("lon", -84.5), ("label", "Atlanta"));

        Assert.Equal(4, Assert.Single(NativeShapes.Paths(cell)).Length);
        Assert.Equal(2, NativeShapes.Drawn("setConvergence", [L(cell)]));
    }

    [Fact]
    public void Aurora_ShouldTurnTheChanceGridIntoAreas_WithStrongerChancesOnTop()
    {
        // 2 rows x 4 columns north of 60.5: row 0 has a run of 2 (5%, 40%), row 1 one cell (10%).
        var north = Convert.ToBase64String([0, 5, 40, 0, 0, 0, 0, 10]);
        var aurora = O(("rows", 2.0), ("columns", 4.0), ("edge", 60.0), ("north", north), ("south", Convert.ToBase64String(new byte[8])));

        var cells = NativeShapes.AuroraCells(aurora);

        Assert.Equal(3, cells.Count);
        Assert.Contains(cells, c => !c.Strong && c.West == -90 && c.East == 90 && c.North == 90);
        Assert.Contains(cells, c => c.Strong && c.West == 0 && c.East == 90);
        Assert.Equal(3, NativeShapes.Drawn("setAurora", [aurora]));
        Assert.Equal(0, NativeShapes.Drawn("setAurora", [O(("rows", 2.0), ("columns", 4.0), ("edge", 60.0), ("north", "not base64!"))]));
    }

    [Fact]
    public void TimeMachine_ShouldPinItsIncidentsHazardsAndTracks_AndShowItsRadar()
    {
        var snapshot = O(("radarTileUrl", "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/ridge::USCOMP-N0Q-202611131200/{z}/{x}/{y}.png"),
            ("incidents", L(O(("lat", 33.7), ("lon", -84.4)))), ("earthquakes", L(O(("lat", 35.0), ("lon", -118.0)))),
            ("tracks", L(O(("lat", 33.6), ("lon", -84.3), ("category", "aircraft")))));

        Assert.Equal(3, NativeShapes.TimeMachinePins(snapshot).Length);
        Assert.Equal(4, NativeShapes.Drawn("setTimeMachine", ["tg-viewport", snapshot]));
    }

    [Fact]
    public void LiveFlowAndSettings_ShouldCountAsDrawnOnlyWhenOn()
    {
        Assert.Equal(1, NativeShapes.Drawn("setTrafficFlow", ["tg-viewport", true]));
        Assert.Equal(0, NativeShapes.Drawn("setTrafficFlow", ["tg-viewport", false]));
        Assert.Equal(1, NativeShapes.Drawn("setNightLights", [O(("url", "https://gibs.example/{z}/{x}/{y}.png"))]));
    }

    [Fact]
    public void Pins_ShouldStillDraw_AndBadCoordinatesShouldNot()
    {
        Assert.Equal(2, NativeShapes.Drawn("setHazards", [L(O(("lat", 1.0), ("lon", 2.0)), O(("latitude", 3.0), ("longitude", 4.0)), O(("lat", 95.0), ("lon", 0.0)))]));
        Assert.False(NativeShapes.AsDouble(true, out _));
    }

    [Theory]
    [InlineData("setCameraPins", null, null, null, "camera.fill")]
    [InlineData("setAircraft", null, null, null, "airplane")]
    [InlineData("setShips", null, null, "Sailing", "sailboat.fill")]
    [InlineData("setShips", null, null, "Cargo", "shippingbox.fill")]
    [InlineData("setTrafficIncidents", null, "crash", null, "car.side.fill")]
    [InlineData("setTrafficIncidents", null, "closure", null, "exclamationmark.octagon.fill")]
    [InlineData("setHazards", "earthquake", null, null, "waveform.path.ecg")]
    [InlineData("setHazards", "wildfire", null, null, "flame.fill")]
    [InlineData("setHazards", "cyclone", null, null, "hurricane")]
    [InlineData("setHazards", "flood", null, null, "water.waves")]
    [InlineData("setConflict", "conflict-confirmed", null, null, "target")]
    [InlineData("setSatellites", null, null, null, "antenna.radiowaves.left.and.right.circle.fill")]
    [InlineData("setWorldNews", null, null, null, "newspaper.fill")]
    public void Markers_ShouldUseTheObjectType_NotLetters(string operation, string? kind, string? category, string? type, string symbol)
    {
        var item = O(("kind", kind), ("category", category), ("type", type));

        Assert.Equal(symbol, NativeShapes.Marker(operation, item).Symbol);
    }

    [Fact]
    public void EmergencyAircraftAndMajorItems_ShouldStandOut()
    {
        var aircraft = NativeShapes.Marker("setAircraft", O(("emergency", "General emergency (7700)")));
        var airport = NativeShapes.Marker("setAirportStatus", O(("severity", "major")));

        Assert.Equal("airplane.circle.fill", aircraft.Symbol);
        Assert.Equal(NativeShapes.MarkerTone.Red, aircraft.Tone);
        Assert.True(aircraft.Prominent);
        Assert.Equal(NativeShapes.MarkerTone.Red, airport.Tone);
        Assert.True(airport.Prominent);
    }

    [Fact]
    public void Identity_ShouldStayStableWhenAMovingObjectsPositionChanges()
    {
        var first = O(("id", "abc123"), ("lat", 33.7), ("lon", -84.4), ("heading", 90.0));
        var moved = O(("id", "abc123"), ("lat", 33.8), ("lon", -84.2), ("heading", 110.0));

        Assert.Equal(NativeShapes.Identity("setAircraft", first), NativeShapes.Identity("setAircraft", moved));
        Assert.Equal("setAircraft:id:abc123", NativeShapes.Identity("setAircraft", moved));
    }

    [Fact]
    public void EveryLayerOperation_ShouldBeDrawnByAHandlerOrAsPointsAndPaths()
    {
        // Every set operation the page sends the Mac is either one of the bridge's own handlers or
        // read by NativeShapes as points and paths; a new one is a reminder to check the Mac.
        var pointsAndPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "setCameraPins", "setWeatherSnapshot", "setHourlyForecast", "setWeatherAlerts", "setCameraAlertHighlights", "setTrafficIncidents",
            "setWorldNews", "setNewsShading", "setMapNotes", "setDriveFollow", "setSelectModeEnabled", "setRoadWeather", "setMessageSigns",
            "setRoadTraffic", "setAircraft", "setAirQuality", "setPowerOutages", "setInternetOutages", "setConvergence", "setAirportStatus",
            "setChokepoints", "setWeatherWarnings", "setSatellites", "setTravelAdvice", "setFireHotspots", "setConflict", "setHumanitarian", "setHazards",
            "setShips", "setCoverageMarkers", "setRoute", "setRouteHighlights", "setRouteConditions", "setStreetViewMarker", "setPhoto",
        };
        var sets = NativeOverwatchOperations.All.Where(op => op.StartsWith("set", StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(sets, op => !NativeShapes.Handled.Contains(op) && !pointsAndPaths.Contains(op));
    }

    // 19-11-26: the Mac map's camera pins say what the camera offers, as the web map's shapes do.
    [Fact]
    public void CameraPins_ShouldShowStillImage_LiveVideo_AndNotWorking_BySymbol()
    {
        Assert.Equal("camera.fill", NativeShapes.Marker("setCameraPins", O(("mode", "StillImage"), ("state", "AvailableNow"))).Symbol);
        Assert.Equal("video.fill", NativeShapes.Marker("setCameraPins", O(("mode", "LiveVideo"), ("state", "Unconfirmed"))).Symbol);
        Assert.Equal("exclamationmark.triangle.fill", NativeShapes.Marker("setCameraPins", O(("mode", "StillImage"), ("state", "Unavailable"))).Symbol);
        Assert.Equal("camera.fill", NativeShapes.Marker("setCameraPins", O(("name", "Old payload"))).Symbol);
    }

    // 19-11-26: the Mac map uses the web globe's colours, with a dark glyph on the bright ones.
    [Fact]
    public void MacMapPalette_ShouldMatchTheWebGlobe()
    {
        Assert.Equal("#59f07a", NativeShapes.ToneHex(NativeShapes.MarkerTone.Green));
        Assert.Equal("#59f07a", NativeShapes.ToneHex(NativeShapes.MarkerTone.Purple));
        Assert.Equal("#ffb000", NativeShapes.ToneHex(NativeShapes.MarkerTone.Orange));
        Assert.Equal("#ff3b3b", NativeShapes.ToneHex(NativeShapes.MarkerTone.Red));
        Assert.True(NativeShapes.DarkGlyph(NativeShapes.MarkerTone.Green));
        Assert.False(NativeShapes.DarkGlyph(NativeShapes.MarkerTone.Red));

        var js = File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")),
            "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        foreach (var tone in new[] { NativeShapes.MarkerTone.Green, NativeShapes.MarkerTone.Purple, NativeShapes.MarkerTone.Red, NativeShapes.MarkerTone.Orange })
            Assert.Contains(NativeShapes.ToneHex(tone), js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#c77dff", js);

        var bridge = File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")),
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));
        Assert.DoesNotContain("UIColor.System", bridge);
        Assert.Contains("_map.OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;", bridge);
        Assert.Contains("_map.AddOverlay(HoloMap.Veil(), MKOverlayLevel.AboveLabels);", bridge);
    }

    // 19-11-26 (Grant): the web and Mac Overwatch identical, in his option B (Holo Wireframe). The web
    // globe's HOLO numbers, the Mac's HoloMap and OverwatchMapLook must be one look.
    [Fact]
    public void BothMaps_ShouldDrawTheSameHoloLook()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var js = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var holo = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.App/Platforms/MacCatalyst/HoloMap.cs"));
        string N(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains($"green: '{OverwatchMapLook.Green}',", js);
        Assert.Contains($"globe: '{OverwatchMapLook.Globe}',", js);
        Assert.Contains($"landAlpha: {N(OverwatchMapLook.LandAlpha)},", js);
        Assert.Contains($"coastAlpha: {N(OverwatchMapLook.CoastAlpha)},", js);
        Assert.Contains($"gridAlpha: {N(OverwatchMapLook.GridAlpha)},", js);
        Assert.Contains($"gridStep: {OverwatchMapLook.GridStep},", js);
        Assert.Contains($"streetsBelowMetres: {N(OverwatchMapLook.StreetsBelowMetres)},", js);
        Assert.Contains($"streetsFullMetres: {N(OverwatchMapLook.StreetsFullMetres)},", js);
        Assert.Contains($"streetAlpha: {N(OverwatchMapLook.StreetAlpha)},", js);
        Assert.Contains($"washAlpha: {N(OverwatchMapLook.WashAlpha)}", js);
        Assert.Contains($"baseLayer.brightness = {N(OverwatchMapLook.StreetBrightness)};", js);
        Assert.Contains("Canvas/World_Dark_Gray_Base/MapServer'", js);
        Assert.Contains("Canvas/World_Dark_Gray_Base/MapServer/tile/", OverwatchMapLook.StreetTiles);
        Assert.Contains($"fetch('{OverwatchMapLook.LandPath}'", js);
        Assert.Contains("await applyHoloLook(viewer, baseLayer);", js);
        Assert.Contains("OverwatchMapLook.StreetTiles", holo);
        Assert.Contains("OverwatchMapLook.WashAlpha", holo);
        Assert.Contains("MKOverlayLevel.AboveLabels", File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs")));

        Assert.Equal(0, OverwatchMapLook.StreetShare(1_000_000));
        Assert.Equal(1, OverwatchMapLook.StreetShare(10_000));
        Assert.Equal(0.5, OverwatchMapLook.StreetShare(230_000), 3);

        // Night Ops zoom stages (20-11-26): one set of heights for both maps.
        Assert.Contains($"orbitAboveMetres: {N(OverwatchMapLook.OrbitAboveMetres)}, regionalAboveMetres: {N(OverwatchMapLook.RegionalAboveMetres)}, approachAboveMetres: {N(OverwatchMapLook.ApproachAboveMetres)}", js);
        Assert.Equal(["orbit", "regional", "approach", "ground"], new[] { 9_000_000d, 400_000, 20_000, 1_200 }.Select(OverwatchMapLook.Stage));

        // The HUD's sun (phase 4): one formula in both places, close to the real sky. At the June
        // solstice noon on the equator and prime meridian the sun is due north at about 66.6 degrees.
        Assert.Contains("const g = (357.529 + 0.98560028 * d) * rad, q = 280.459 + 0.98564736 * d;", js);
        Assert.Contains("24.06570982441908 * d", js);
        var (azimuth, elevation) = OverwatchMapLook.SunPosition(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero), 0, 0);
        Assert.InRange(elevation, 65.5, 67.5);
        Assert.True(azimuth < 10 || azimuth > 350, $"azimuth {azimuth}");
        Assert.True(OverwatchMapLook.SunPosition(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero), 0, 0).Elevation < -40, "midnight is below the horizon");

        // Night mode (phases 5/6): one rule for both maps, and the Mac's dimming as strong as the web's
        // CSS brightness filter (brightness = 1 - dim).
        Assert.Contains("shell.dataset.owNight = sun.el < -6 ? 'night' : sun.el < 0 ? 'dusk' : 'day';", js);
        Assert.Equal(["day", "dusk", "dusk", "night"], new[] { 10d, -0.5, -5.9, -6.1 }.Select(OverwatchMapLook.Night));
        Assert.Equal([0, OverwatchMapLook.DuskDim, OverwatchMapLook.NightDim], new[] { "day", "dusk", "night" }.Select(OverwatchMapLook.Dim));
        var css = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor.css"));
        Assert.Contains($"filter: brightness({N(Math.Round(1 - OverwatchMapLook.DuskDim, 2))});", css);
        Assert.Contains($"filter: brightness({N(Math.Round(1 - OverwatchMapLook.NightDim, 2))}) contrast(1.12);", css);

        // Looks (21-11-26): only Satellite hides the holo globe, and anything unknown is Night Ops.
        Assert.Equal(["standard", "night-ops", "holo-earth", "satellite", "night-ops", "night-ops"], new[] { "standard", "night-ops", "holo-earth", "satellite", "nonsense", null }.Select(OverwatchMapLook.Look));
        Assert.Equal([true, true, true, false, true], new[] { "standard", "night-ops", "holo-earth", "satellite", null }.Select(OverwatchMapLook.ShowsHolo));
        Assert.Contains("const LOOKS = ['standard', 'night-ops', 'holo-earth', 'satellite'];", js);
        // The HUD's ground height leaves out terrain-skirt readings (GND -21443 m over Atlanta, 21-11-26).
        Assert.Contains("const GROUND_MIN = -500, GROUND_MAX = 9000;", js);
        Assert.Contains("h >= GROUND_MIN && h <= GROUND_MAX) ground = Math.round(h);", js);
        // The time machine bar covers the weather card's corner, so the card steps aside while it's open.
        Assert.Contains(".tg-shell:has(.tg-time-machine) .tg-weather-panel { display: none !important; }",
            File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/tactical-globe.css")));

        using var land = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/data/overwatch-land.json")));
        Assert.True(land.RootElement.GetProperty("rings").GetArrayLength() > 200, "both maps draw the land from this one file");
    }

    // Crowds become counts on the Mac (19-11-26): Apple's grouping crashed the app.
    [Fact]
    public void Group_ShouldTurnCrowdsIntoCounts_AndLeaveLonePinsAndCalloutsAlone()
    {
        var items = new List<Dictionary<string, object?>>();
        for (var i = 0; i < 45; i++) items.Add(O(("id", $"a{i}"), ("lat", 33.70 + i * 0.0001), ("lon", -84.40)));
        items.Add(O(("id", "lone"), ("lat", 40.0), ("lon", -74.0)));
        items.Add(O(("id", "boxed"), ("lat", 33.70), ("lon", -84.40), ("callout", "ATLANTA · 12 SOURCES")));

        var grouped = NativeShapes.Group(items, 0.5, 0.5, "cameras");

        var count = Assert.Single(grouped, g => g.ContainsKey("count"));
        Assert.Equal(45, count["count"]);
        Assert.Equal("45 cameras", count["title"]);
        Assert.StartsWith("group:", (string)count["id"]!);
        Assert.Contains(grouped, g => (string?)g["id"] == "lone");
        Assert.Contains(grouped, g => (string?)g["id"] == "boxed");
        Assert.Equal(10, NativeShapes.Group(items.Take(10).ToList(), 0.5, 0.5, "cameras").Count);
    }

    [Fact]
    public void Focus_ShouldNameRealLayersOnBothApps_AndCalloutsPickTheMostSerious()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var js = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        foreach (var layer in OverwatchFocus.All)
        {
            foreach (var source in layer.WebSources) Assert.Contains($"new Cesium.CustomDataSource('{source}')", js);
            Assert.All(layer.NativeOperations, op => Assert.Contains(op, NativeOverwatchOperations.All));
        }
        Assert.Contains(NativeOverwatchOperations.FocusLayer, NativeOverwatchOperations.All);
        Assert.Null(NativeShapes.FocusOperations([null]));
        Assert.Equal(new HashSet<string> { "setWorldNews" }, NativeShapes.FocusOperations([O(("web", new object?[] { "tg-world-news" }), ("native", new object?[] { "setWorldNews" }))]));

        var picked = OverwatchCallouts.Pick(new[] { ("a", "minor", 9), ("b", "major", 1), ("c", "moderate", 5), ("d", "minor", 20) },
            x => OverwatchCallouts.SeverityRank(x.Item2), x => x.Item3);
        Assert.Equal(new[] { "b", "c", "d" }.ToHashSet(), picked.Select(x => x.Item1).ToHashSet());
    }

    // 20-11-26 (Grant: every layer visible on the Mac app and the web app): each layer's sample, as
    // the page sends it, gives the Mac map something to draw. The web globe's check uses the same
    // samples (OverwatchGridScriptBrowserTests.EveryLayer_ShouldDrawOnTheWebGlobe).
    [Theory]
    [MemberData(nameof(OverwatchLayerSample.Names), MemberType = typeof(OverwatchLayerSample))]
    public void EveryLayer_ShouldDrawOnTheMacMap(string layer)
    {
        var sample = OverwatchLayerSample.Named(layer);
        var arguments = sample.MacArguments();
        if (sample.NeedsCameras)
        {
            // The page's Mac adapter adds each highlighted camera's place (tactical-globe.js).
            var cameras = (object?[])OverwatchLayerSample.Named("Cameras").MacArguments()[0]!;
            var places = cameras.OfType<Dictionary<string, object?>>().ToDictionary(c => (string)c["id"]!, StringComparer.Ordinal);
            arguments = [((object?[])arguments[0]!).OfType<Dictionary<string, object?>>()
                .Select(h => new Dictionary<string, object?>(h, StringComparer.Ordinal) { ["lat"] = places[(string)h["cameraId"]!]["lat"], ["lon"] = places[(string)h["cameraId"]!]["lon"] })
                .ToArray<object?>()];
        }

        Assert.True(NativeShapes.Drawn(sample.Op, arguments) > 0, $"{layer} ({sample.Op}) draws nothing on the Mac map");
    }
}
