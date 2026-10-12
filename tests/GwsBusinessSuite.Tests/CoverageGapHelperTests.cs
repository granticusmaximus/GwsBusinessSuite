using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Tests;

public sealed class CoverageGapHelperTests
{
    [Fact]
    public void Nearest_ShouldRankRegionsByDistanceFromTheViewCentre_WithDirection()
    {
        // A view over central Alabama: no cameras there.
        var view = new BoundingBox(33.0, 32.0, -86.0, -87.5);

        var nearest = CoverageGapHelper.Nearest(view, CameraCoverageRegions.All);

        nearest.Should().HaveCount(3);
        nearest[0].Region.Name.Should().Be("Georgia (GDOT)");
        nearest[0].Direction.Should().Be("northeast");
        nearest[0].Miles.Should().BeInRange(150, 175);
        nearest.Select(s => s.Miles).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Nearest_ShouldSkipRegionsInsideTheView()
    {
        var overAtlanta = new BoundingBox(34.0, 33.5, -84.0, -84.7);

        CoverageGapHelper.Nearest(overAtlanta, CameraCoverageRegions.All).Select(s => s.Region.Name).Should().NotContain("Georgia (GDOT)");
    }

    [Theory]
    [InlineData(40, -100, 41, -100, "north")]
    [InlineData(40, -100, 40, -99, "east")]
    [InlineData(40, -100, 39, -101, "southwest")]
    public void Direction_ShouldBeACompassPoint(double lat1, double lon1, double lat2, double lon2, string expected) =>
        CoverageGapHelper.Direction(lat1, lon1, lat2, lon2).Should().Be(expected);

    [Fact]
    public void Miles_ShouldMatchAKnownDistance() =>
        CoverageGapHelper.Miles(33.75, -84.39, 47.60, -122.33).Should().BeApproximately(2180, 15, "Atlanta to Seattle");
}
