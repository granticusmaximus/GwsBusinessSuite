using FluentAssertions;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// 19-11-26: client impact alerts. What near or named news means for a client.
public sealed class ClientRadarTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Match_ShouldFlagHazardsAlertsAndNewsNearby_AndStoriesNamingTheCompany()
    {
        var client = new Contact { FullName = "Ann Lee", Company = "Peachtree Logistics LLC", Latitude = 33.75, Longitude = -84.39 };
        var near = new HazardFeature("h1", "flood", "Flooding on the Chattahoochee", "River above flood stage", 33.9, -84.5, "moderate", "https://water.example", Now);
        var far = new HazardFeature("h2", "wildfire", "Fire in California", "", 37.0, -120.0, "major", "", Now);
        var alert = new WeatherAlert("a1", "Tornado Warning", "Extreme", "Fulton County", "", []);
        var story = new WorldNewsMapSignal("s1", "Atlanta water main break", "Roads closed downtown",
            new WorldNewsLocation("US", "United States", "Atlanta", 33.76, -84.39, WorldNewsPrecision.City), 1, 4, 4, Now, null, []);
        EventOverview Headline(string id, string text) => new(id, text, 3, 3, ["us"], ["en"], Now, Now, text);

        var impacts = ClientRadarService.Match(client, new ClientRadarService.Nearby([near, far], [alert], [story]),
            [Headline("e1", "Peachtree Logistics wins port contract"), Headline("e2", "Peach harvest falls")], Now);

        impacts.Select(i => i.Kind).Should().BeEquivalentTo([ClientImpactKinds.Hazard, ClientImpactKinds.Weather, ClientImpactKinds.News, ClientImpactKinds.Named]);
        impacts.Should().NotContain(i => i.Title == "Fire in California", "it's over 50 miles away");
        impacts.Single(i => i.Kind == ClientImpactKinds.Named).Title.Should().Be("Peachtree Logistics wins port contract");
        impacts.First().Miles.Should().BeLessThan(2, "nearest first");
    }

    [Fact]
    public void CompanyPattern_ShouldDropSuffixes_AndIgnoreNamesTooShortToBeSafe()
    {
        ClientRadarService.CompanyPattern("Acme Corp.")!.IsMatch("Acme recalls widgets").Should().BeTrue();
        ClientRadarService.CompanyPattern("Acme Corp.")!.IsMatch("Acmeville floods").Should().BeFalse("whole words only");
        ClientRadarService.CompanyPattern("IBM").Should().BeNull("three letters match too much");
        ClientRadarService.CompanyPattern(null).Should().BeNull();
        ClientRadarService.Match(new Contact { FullName = "Bo" }, new ClientRadarService.Nearby([], [], []), [], Now).Should().BeEmpty();
    }
}
