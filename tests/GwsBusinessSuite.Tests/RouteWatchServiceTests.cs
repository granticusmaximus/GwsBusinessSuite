using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Application.Weather;

namespace GwsBusinessSuite.Tests;

public sealed class RouteWatchServiceTests
{
    // A straight north-south road 1 degree long (~69 miles).
    private static readonly IReadOnlyList<RoutePoint> Road = [new(32.0, -84.0), new(32.5, -84.0), new(33.0, -84.0)];

    [Fact]
    public void Locate_ShouldMeasureMilesAlongAndOffTheRoad()
    {
        var index = new RouteWatchService.RouteIndex(Road);

        index.TotalMiles.Should().BeApproximately(69, 0.5);
        var onRoad = index.Locate(32.5, -84.0);
        onRoad.MilesAlong.Should().BeApproximately(34.5, 0.5);
        onRoad.MilesOff.Should().BeApproximately(0, 0.01);
        var beside = index.Locate(32.25, -84.01);
        beside.MilesOff.Should().BeApproximately(0.58, 0.05, "0.01 degrees of longitude at 32N is about 0.59 miles");
        index.Locate(31.0, -84.0).MilesAlong.Should().Be(0, "a point before the start projects onto the start");
    }

    [Fact]
    public void ParseOsrmRoute_ShouldReadGeometryDistanceAndDuration()
    {
        const string json = """{"code":"Ok","routes":[{"distance":180000.5,"duration":7740,"geometry":{"type":"LineString","coordinates":[[-83.6152,32.461],[-84.388,33.749]]}}]}""";

        var route = RouteWatchService.ParseOsrmRoute(json)!.Value;

        route.Path.Should().Equal(new RoutePoint(32.461, -83.6152), new RoutePoint(33.749, -84.388));
        route.Meters.Should().Be(180000.5);
        RouteWatchService.ParseOsrmRoute("""{"code":"NoRoute","routes":[]}""").Should().BeNull();
    }

    [Fact]
    public void ParseOsrmRoute_ShouldReadEachLegOfAMultiStopTrip()
    {
        const string json = """{"code":"Ok","routes":[{"distance":300000,"duration":10800,"legs":[{"distance":100000,"duration":3600},{"distance":200000,"duration":7200}],"geometry":{"coordinates":[[-83.6,32.4],[-84.0,33.0],[-84.4,33.7]]}}]}""";

        var route = RouteWatchService.ParseOsrmRoute(json)!.Value;

        route.Legs.Should().Equal((100000d, 3600d), (200000d, 7200d));
    }

    [Theory]
    [InlineData("32.46, -83.61", 32.46, -83.61)]
    [InlineData("-41.29,174.78", -41.29, 174.78)]
    public void TryParseCoordinates_ShouldAcceptMapPickedStops(string text, double lat, double lon)
    {
        RouteWatchService.TryParseCoordinates(text).Should().Be(new GwsBusinessSuite.Application.Geocoding.GeocodeResult(lat, lon));
    }

    [Theory]
    [InlineData("Atlanta, GA")]
    [InlineData("95, 200")]
    [InlineData("12 Main St")]
    public void TryParseCoordinates_ShouldLeavePlaceNamesToTheGeocoder(string text)
    {
        RouteWatchService.TryParseCoordinates(text).Should().BeNull();
    }

    [Fact]
    public void TouchesRoute_ShouldNeedARoutePointInsideTheAlertPolygon()
    {
        var overRoad = new WeatherAlert("a", "Flood Warning", "Severe", "Area", "", [[(-84.1, 32.4), (-83.9, 32.4), (-83.9, 32.6), (-84.1, 32.6)]]);
        var offRoad = new WeatherAlert("b", "Heat Advisory", "Moderate", "Elsewhere", "", [[(-82.1, 32.4), (-81.9, 32.4), (-81.9, 32.6), (-82.1, 32.6)]]);

        RouteWatchService.TouchesRoute(overRoad, Road).Should().BeTrue();
        RouteWatchService.TouchesRoute(offRoad, Road).Should().BeFalse();
    }

    [Fact]
    public void ThinAlongRoute_ShouldKeepTheEndsAndAnEvenSpread()
    {
        var cameras = Enumerable.Range(0, 200)
            .Select(i => new RouteCamera(new CameraFeed($"c{i}", $"c{i}", 0, 0, "u", CameraStreamKind.Snapshot, "s", "u"), i, 0))
            .ToList();

        var thinned = RouteWatchService.ThinAlongRoute(cameras, 10);

        thinned.Should().HaveCount(10);
        thinned[0].MilesAlong.Should().Be(0);
        thinned[^1].MilesAlong.Should().Be(199);
        thinned.Select(c => c.MilesAlong).Should().BeInAscendingOrder();
    }
}
