using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Tests;

public sealed class CameraCoverageRegionsTests
{
    [Fact]
    public void All_ShouldContainTheKnownZeroRegistrationRegions()
    {
        var names = CameraCoverageRegions.All.Select(r => r.Name).ToList();

        names.Should().Contain(n => n.Contains("Georgia"));
        names.Should().Contain(n => n.Contains("Washington"));
        names.Should().Contain(n => n.Contains("Austin"));
        names.Should().Contain(n => n.Contains("California"));
        names.Should().Contain(n => n.Contains("Ontario"));
        names.Should().Contain(n => n.Contains("Ottawa"));
        names.Should().Contain(n => n.Contains("Toronto"));
        names.Should().Contain(n => n.Contains("London"));
    }

    [Fact]
    public void All_ShouldHaveValidCoordinatesAndPositiveFlyToHeight()
    {
        CameraCoverageRegions.All.Should().NotBeEmpty();

        foreach (var region in CameraCoverageRegions.All)
        {
            region.Latitude.Should().BeInRange(-90, 90, $"{region.Name}'s latitude must be valid");
            region.Longitude.Should().BeInRange(-180, 180, $"{region.Name}'s longitude must be valid");
            region.FlyToHeightMeters.Should().BePositive($"{region.Name}'s fly-to height must be positive");
            region.Name.Should().NotBeNullOrWhiteSpace();
            region.SourceName.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void All_ShouldNotIncludeWindy()
    {
        // Windy's webcams are globally scattered, not a fixed region - deliberately excluded,
        // see CameraCoverageRegion.cs's own comment.
        CameraCoverageRegions.All.Should().NotContain(r => r.SourceName == "Windy");
    }
}
