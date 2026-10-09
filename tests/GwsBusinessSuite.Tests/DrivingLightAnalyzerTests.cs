using FluentAssertions;
using GwsBusinessSuite.Application.RouteWatch;

namespace GwsBusinessSuite.Tests;

public sealed class DrivingLightAnalyzerTests
{
    [Fact]
    public void SunPosition_AtGreenwichOnTheJuneSolsticeAtNoon_ShouldBeAbout62DegreesHighDueSouth()
    {
        // 90 - latitude + axial tilt = 90 - 51.48 + 23.44 = 61.96.
        var (azimuth, elevation) = DrivingLightAnalyzer.SunPosition(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero), 51.4769, 0);

        elevation.Should().BeApproximately(61.96, 0.5);
        azimuth.Should().BeApproximately(180, 3);
    }

    [Fact]
    public void SunPosition_OnTheEquatorAtTheMarchEquinoxNoon_ShouldBeNearlyOverhead()
    {
        var (_, elevation) = DrivingLightAnalyzer.SunPosition(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero), 0, 0);

        elevation.Should().BeGreaterThan(88);
    }

    [Fact]
    public void SunPosition_AtMidnightInDecember_ShouldBeWellBelowTheHorizon()
    {
        var (_, elevation) = DrivingLightAnalyzer.SunPosition(new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero), 51.4769, 0);

        elevation.Should().BeLessThan(-50);
    }

    [Fact]
    public void SunPosition_ShortlyAfterSunriseInGeorgia_ShouldBeLowInTheEast()
    {
        // Macon, GA on 2026-10-09 at 8:10 AM EDT - about half an hour after sunrise.
        var (azimuth, elevation) = DrivingLightAnalyzer.SunPosition(new DateTimeOffset(2026, 10, 9, 12, 10, 0, TimeSpan.Zero), 32.84, -83.63);

        elevation.Should().BeInRange(2, 12);
        azimuth.Should().BeInRange(90, 110);
    }

    [Fact]
    public void Analyze_DrivingEastJustAfterSunrise_ShouldFlagGlare_ButNotTheSameRoadWestbound()
    {
        var departure = new DateTimeOffset(2026, 10, 9, 12, 10, 0, TimeSpan.Zero);
        var eastbound = StraightRoad(32.84, -83.90, -83.40);
        var westbound = eastbound.AsEnumerable().Reverse().ToList();

        var east = DrivingLightAnalyzer.Analyze(eastbound, legs: null, totalDurationMinutes: 30, departure);
        var west = DrivingLightAnalyzer.Analyze(westbound, legs: null, totalDurationMinutes: 30, departure);

        east.Should().ContainSingle(s => s.Kind == DrivingLightKind.SunGlare);
        var glare = east.Single();
        glare.Heading.Should().Be("EASTBOUND");
        glare.StartMile.Should().BeApproximately(0, 0.5);
        glare.StartUtc.Should().Be(departure);
        glare.Path.Should().NotBeEmpty();
        west.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_AtNight_ShouldReportOneDarkStretchForTheWholeDrive()
    {
        var departure = new DateTimeOffset(2026, 10, 10, 3, 0, 0, TimeSpan.Zero); // 11 PM EDT

        var stretches = DrivingLightAnalyzer.Analyze(StraightRoad(32.84, -83.90, -83.40), null, 30, departure);

        stretches.Should().ContainSingle();
        stretches[0].Kind.Should().Be(DrivingLightKind.Dark);
        stretches[0].EndUtc.Should().BeCloseTo(departure.AddMinutes(30), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MinutesAtMile_ShouldFollowEachLegsOwnPace()
    {
        var legs = new[]
        {
            new RouteLeg("A", "B", DistanceMiles: 10, DurationMinutes: 10, StartsAtMile: 0),
            new RouteLeg("B", "C", DistanceMiles: 10, DurationMinutes: 30, StartsAtMile: 10)
        };

        DrivingLightAnalyzer.MinutesAtMile(5, legs, totalMiles: 20, totalDurationMinutes: 40).Should().BeApproximately(5, 0.001);
        DrivingLightAnalyzer.MinutesAtMile(15, legs, totalMiles: 20, totalDurationMinutes: 40).Should().BeApproximately(25, 0.001);
        DrivingLightAnalyzer.MinutesAtMile(20, legs, totalMiles: 20, totalDurationMinutes: 40).Should().BeApproximately(40, 0.001);
    }

    private static List<RoutePoint> StraightRoad(double latitude, double fromLongitude, double toLongitude) =>
        Enumerable.Range(0, 51)
            .Select(i => new RoutePoint(latitude, fromLongitude + (toLongitude - fromLongitude) * i / 50.0))
            .ToList();
}
