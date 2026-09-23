using FluentAssertions;
using GwsBusinessSuite.Application.Weather;

namespace GwsBusinessSuite.Tests;

public sealed class GeoPolygonTests
{
    // A 1x1 degree square, CCW winding, centered near the equator - real polygon coordinates
    // (not degenerate) to exercise the actual crossing-number math.
    private static readonly IReadOnlyList<(double Longitude, double Latitude)> SquareRingCcw =
    [
        (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)
    ];

    // Same square, opposite winding order - Contains must not care which way a ring winds.
    private static readonly IReadOnlyList<(double Longitude, double Latitude)> SquareRingCw =
    [
        (0.0, 0.0), (0.0, 1.0), (1.0, 1.0), (1.0, 0.0)
    ];

    // A smaller square hole punched out of the middle of SquareRingCcw.
    private static readonly IReadOnlyList<(double Longitude, double Latitude)> HoleRing =
    [
        (0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75)
    ];

    [Fact]
    public void Contains_ShouldReturnTrue_ForAPointInsideASimpleSquareRing()
    {
        var rings = new[] { SquareRingCcw };

        GeoPolygon.Contains(rings, latitude: 0.5, longitude: 0.5).Should().BeTrue();
    }

    [Fact]
    public void Contains_ShouldReturnFalse_ForAPointOutsideASimpleSquareRing()
    {
        var rings = new[] { SquareRingCcw };

        GeoPolygon.Contains(rings, latitude: 5.0, longitude: 5.0).Should().BeFalse();
    }

    [Fact]
    public void Contains_ShouldReturnFalse_ForAPointInsideAHole()
    {
        var rings = new[] { SquareRingCcw, HoleRing };

        // Inside the outer ring, but also inside the hole ring punched into it.
        GeoPolygon.Contains(rings, latitude: 0.5, longitude: 0.5).Should().BeFalse();
    }

    [Fact]
    public void Contains_ShouldReturnTrue_ForAPointInsideTheOuterRingButOutsideTheHole()
    {
        var rings = new[] { SquareRingCcw, HoleRing };

        // Inside the outer ring, but not inside the smaller hole ring.
        GeoPolygon.Contains(rings, latitude: 0.1, longitude: 0.1).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.5, 0.5, true)]
    [InlineData(5.0, 5.0, false)]
    public void Contains_ShouldBeWindingOrderAgnostic(double latitude, double longitude, bool expected)
    {
        var ccwResult = GeoPolygon.Contains([SquareRingCcw], latitude, longitude);
        var cwResult = GeoPolygon.Contains([SquareRingCw], latitude, longitude);

        ccwResult.Should().Be(expected);
        cwResult.Should().Be(expected);
    }

    [Fact]
    public void Contains_ShouldOnlyMatchTheRelevantPolygon_InAMultiPolygonStyleRingSet()
    {
        // Simulates a MultiPolygon alert: two disjoint polygons, each contributed as its own
        // independent ring (mirrors how WeatherAlert.Rings is actually populated for a
        // MultiPolygon feature - see NwsAlertsServiceTests).
        var farAwayRing = new List<(double Longitude, double Latitude)>
        {
            (10.0, 45.0), (10.5, 45.0), (10.5, 45.5), (10.0, 45.5)
        };
        var rings = new[] { SquareRingCcw, farAwayRing };

        GeoPolygon.Contains(rings, latitude: 0.5, longitude: 0.5).Should().BeTrue();
        GeoPolygon.Contains(rings, latitude: 45.25, longitude: 10.25).Should().BeTrue();
        GeoPolygon.Contains(rings, latitude: 20.0, longitude: 20.0).Should().BeFalse();
    }

    [Fact]
    public void Contains_ShouldReturnFalse_ForAnEmptyRingsList()
    {
        GeoPolygon.Contains([], latitude: 0.5, longitude: 0.5).Should().BeFalse();
    }

    [Fact]
    public void Contains_ShouldReturnFalse_ForARingWithFewerThanThreeVertices()
    {
        var degenerateRing = new[] { (0.0, 0.0), (1.0, 1.0) };

        GeoPolygon.Contains([degenerateRing], latitude: 0.5, longitude: 0.5).Should().BeFalse();
    }
}
