using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

// Overwatch analyst tools (13-11-26).
public sealed class AnalystGeometryTests
{
    [Fact]
    public void LondonToParis_ShouldBeAbout344KmSouthEast()
    {
        var leg = AnalystGeometry.Legs([(51.5074, -0.1278), (48.8566, 2.3522)]).Single();
        leg.Kilometres.Should().BeApproximately(343.5, 2);
        leg.BearingDegrees.Should().BeApproximately(148, 2);
        AnalystGeometry.Describe(leg).Should().Contain("km").And.Contain("SSE");
    }

    [Fact]
    public void ARing_ShouldStayItsRadiusFromTheCentre_AndClose()
    {
        var ring = AnalystGeometry.Ring(33.749, -84.388, 25);
        ring.Should().HaveCount(AnalystGeometry.RingSegments + 1);
        ring.Should().OnlyContain(p => Math.Abs(AnalystGeometry.Kilometres(33.749, -84.388, p.Lat, p.Lon) - 25) < 0.01);
        ring[0].Lat.Should().BeApproximately(ring[^1].Lat, 1e-9);
    }

    [Fact]
    public void ARingAcrossTheDateLine_ShouldKeepLongitudesInRange() =>
        AnalystGeometry.Ring(0, 179.9, 100).Should().OnlyContain(p => p.Lon >= -180 && p.Lon <= 180);

    [Theory]
    [InlineData("5, 25 100", new[] { 5.0, 25, 100 })]
    [InlineData("100,5,5,-3,abc,9000", new[] { 5.0, 100 })]
    [InlineData("", new double[0])]
    public void ParseRings_ShouldKeepSensibleRadiiInOrder(string text, double[] expected) =>
        AnalystGeometry.ParseRings(text).Should().Equal(expected);
}
