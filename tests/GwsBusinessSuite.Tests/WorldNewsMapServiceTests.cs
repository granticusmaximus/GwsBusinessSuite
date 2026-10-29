using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class WorldNewsMapServiceTests
{
    [Theory]
    [InlineData("Japan launches evacuation after earthquake", "Japan")]
    [InlineData("Türkiye election enters final round", "Turkey")]
    [InlineData("South Korean parliament votes", "South Korea")]
    [InlineData("U.S. airports recover after outage", "United States")]
    public void Locate_ShouldMapOnlyExplicitlyNamedCountries(string headline, string expected)
    {
        var result = WorldNewsMapService.Locate(headline, headline);

        result.Should().NotBeNull();
        result!.Country.Should().Be(expected);
        result.Latitude.Should().BeInRange(-90, 90);
        result.Longitude.Should().BeInRange(-180, 180);
    }

    [Fact]
    public void Locate_ShouldNotInventALocationWhenHeadlineHasNone()
    {
        WorldNewsMapService.Locate("Markets rally after rate decision", "Investors assess the announcement")
            .Should().BeNull("a report's publisher country is not evidence of the event's location");
    }

    [Fact]
    public void Locate_ShouldPreferTheLongerCountryName()
    {
        WorldNewsMapService.Locate("South Africa prepares for election", "Latest from South Africa")!.Country
            .Should().Be("South Africa");
    }
}
