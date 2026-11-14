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
            "setChokepoints", "setSatellites", "setTravelAdvice", "setFireHotspots", "setConflict", "setHumanitarian", "setHazards",
            "setShips", "setCoverageMarkers", "setRoute", "setRouteHighlights", "setRouteConditions", "setStreetViewMarker",
        };
        var sets = NativeOverwatchOperations.All.Where(op => op.StartsWith("set", StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(sets, op => !NativeShapes.Handled.Contains(op) && !pointsAndPaths.Contains(op));
    }
}
