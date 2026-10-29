using FluentAssertions;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class WorldNewsContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Backing_ShouldMatchAQuakeNearACityLevelStory_ButNotOneAboutSomethingElse()
    {
        var quake = Hazard(HazardKinds.Earthquake, "M 5.4 - 12 km NE of Tokyo, Japan", 35.75, 139.80);
        var tokyoQuake = Signal("M5.4 quake shakes Tokyo", "Tokyo", "JP", "Japan", 35.69, 139.69);
        var tokyoTrain = Signal("Train derails in Tokyo", "Tokyo", "JP", "Japan", 35.69, 139.69);

        WorldNewsContextService.Backing(tokyoQuake, [quake], [], []).Should().ContainSingle()
            .Which.Should().Match<WorldNewsBacking>(b => b.Source == "USGS" && b.Text.Contains("mi from Tokyo"));
        WorldNewsContextService.Backing(tokyoTrain, [quake], [], []).Should().BeEmpty("a quake doesn't back a train crash");
    }

    [Fact]
    public void Backing_ShouldMatchACountryLevelStoryOnlyByAQuakeNamingTheCountry()
    {
        var japanQuake = Hazard(HazardKinds.Earthquake, "M 6.1 - 80 km E of Miyako, Japan", 39.6, 142.9);
        var chileQuake = Hazard(HazardKinds.Earthquake, "M 5.0 - 30 km W of Arica, Chile", -18.5, -70.6);
        var fire = Hazard(HazardKinds.Wildfire, "Canyon Fire", 36.2, 138.4);
        var story = Signal("Strong earthquake hits northern Japan", null, "JP", "Japan", 36.14, 138.44);

        WorldNewsContextService.Backing(story, [japanQuake, chileQuake, fire], [], []).Select(b => b.Text)
            .Should().Equal("M 6.1 - 80 km E of Miyako, Japan");
    }

    [Fact]
    public void Backing_ShouldMatchWeatherAlertsAndBigOutagesNearACityLevelStory()
    {
        var story = Signal("Flooding and power outage hit Houston", "Houston", "US", "United States", 29.76, -95.37);
        var flood = new WeatherAlert("a1", "Flood Warning", "Severe", "Harris, TX", "2026-10-30T00:00:00Z", []);
        var wind = new WeatherAlert("a2", "Freeze Watch", "Moderate", "Harris, TX", "2026-10-30T00:00:00Z", []);
        var outage = new PowerOutageArea("o1", "Harris", "TX", 45000, ["CenterPoint"], 29.8, -95.4, []);
        var small = new PowerOutageArea("o2", "Fort Bend", "TX", 200, ["CenterPoint"], 29.5, -95.7, []);

        WorldNewsContextService.Backing(story, [], [flood, wind], [outage, small]).Select(b => b.Source)
            .Should().Equal("NWS", "Power outages");
    }

    private static HazardFeature Hazard(string kind, string title, double lat, double lon) =>
        new($"{kind}-{lat}", kind, title, string.Empty, lat, lon, "moderate", "https://example.test/hazard", Now.AddHours(-2));

    private static WorldNewsMapSignal Signal(string title, string? city, string code, string country, double lat, double lon) =>
        new("s1", title, title, new WorldNewsLocation(code, country, city, lat, lon,
                city is null ? WorldNewsPrecision.Country : WorldNewsPrecision.City),
            2, 5, 9, Now, null, []);
}
