using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;

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

    [Theory]
    [InlineData("Tell us what you saw at the stadium")]
    [InlineData("How to polish a turkey before the holidays")]
    public void Locate_ShouldNotMistakeOrdinaryWordsForCountries(string headline)
    {
        WorldNewsMapService.Locate(headline, headline)
            .Should().BeNull("country names are proper nouns, so \"us\", \"polish\" and \"turkey\" don't count");
    }

    [Fact]
    public void Locate_ShouldStillReadAcronyms()
    {
        WorldNewsMapService.Locate("UK and EU resume talks", "")!.Country.Should().Be("United Kingdom");
        WorldNewsMapService.Locate("US Senate passes budget", "")!.Country.Should().Be("United States");
    }

    [Fact]
    public void Place_ShouldMatchWhatTheLayerShows()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var since = now - WorldNewsMapService.Window;
        SituationOverview Situation(string title, int outlets, DateTimeOffset lastSeen) =>
            new("s1", title, title, 2, 9, outlets, ["gb"], ["en"], lastSeen.AddDays(-1), lastSeen);

        WorldNewsMapService.Place(Situation("Japan earthquake aftershocks", 5, now), since)!.Country.Should().Be("Japan");
        WorldNewsMapService.Place(Situation("Japan earthquake aftershocks", 2, now), since)
            .Should().BeNull("two outlets is below the layer's corroboration bar");
        WorldNewsMapService.Place(Situation("Japan earthquake aftershocks", 5, now.AddDays(-2)), since)
            .Should().BeNull("the layer only looks back a day");
        WorldNewsMapService.Place(Situation("Markets rally after rate decision", 5, now), since).Should().BeNull();
    }
}
