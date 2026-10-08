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
        names.Should().Contain(n => n.Contains("Florida"));
        names.Should().Contain(n => n.Contains("Illinois"));
        names.Should().Contain(n => n.Contains("Iowa"));
        names.Should().Contain(n => n.Contains("Oregon"));
        names.Should().Contain(n => n.Contains("New York City"));
        names.Should().Contain(n => n.Contains("Minnesota"));
        names.Should().Contain(n => n.Contains("Nebraska"));
        names.Should().Contain(n => n.Contains("New Zealand"));
        names.Should().Contain(n => n.Contains("Queensland"));
        names.Should().Contain(n => n.Contains("Delaware"));
        names.Should().Contain(n => n.Contains("Rhode Island"));
        names.Should().Contain(n => n.Contains("Virginia"));
        names.Should().Contain(n => n.Contains("Kentucky"));
        names.Should().Contain(n => n.Contains("South Carolina"));
        names.Should().Contain(n => n.Contains("Missouri"));
        names.Should().Contain(n => n.Contains("Michigan"));
        names.Should().Contain(n => n.Contains("North Dakota"));
        names.Should().Contain(n => n.Contains("Hawaii"));
        names.Should().Contain(n => n.Contains("Texas"));
        names.Should().Contain(n => n.Contains("Alabama"));
        names.Should().Contain(n => n.Contains("Tennessee"));
        names.Should().Contain(n => n.Contains("New York Thruway"));
        names.Should().Contain(n => n.Contains("Maine"));
        names.Should().Contain(n => n.Contains("New Hampshire"));
        names.Should().Contain(n => n.Contains("Vermont"));
        names.Should().Contain(n => n.Contains("Arizona"));
        names.Should().Contain(n => n.Contains("Nevada"));
        names.Should().Contain(n => n.Contains("Utah"));
        names.Should().Contain(n => n.Contains("Oklahoma"));
        names.Should().Contain(n => n.Contains("Idaho"));
        names.Should().Contain(n => n.Contains("Iceland"));
        names.Should().Contain(n => n.Contains("Singapore"));
        names.Should().Contain(n => n.Contains("Hong Kong"));
        names.Should().Contain(n => n.Contains("British Columbia"));
        names.Should().Contain(n => n.Contains("Madrid"));
        names.Should().Contain(n => n.Contains("Finland"));
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

    [Fact]
    public void All_ShouldNotIncludeColorado()
    {
        // CDOT (cotrip.org) is disabled in DI - its image endpoint was retired and every camera
        // returned "FEED UNAVAILABLE", see CameraCoverageRegion.cs's own comment.
        CameraCoverageRegions.All.Should().NotContain(r => r.SourceName == "CDOT");
    }
}
