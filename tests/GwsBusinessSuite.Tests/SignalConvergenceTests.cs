using FluentAssertions;
using GwsBusinessSuite.Application.Briefings;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// Overwatch world signals, unit 1 (05-11-26): IODA internet outages and where signals converge.
public sealed class SignalConvergenceTests
{
    private static readonly BoundingBox World = new(90, -90, 180, -180);

    // Trimmed from IODA's live events feed, 05-11-26 (start 1793394000 = 02-11-26 13:00 UTC).
    [Fact]
    public void InternetOutages_ShouldBeGradedByHowManyIndependentSignalsAgree()
    {
        const string json = """
            {"data":[
              {"location":"country/FI","location_name":"Finland","start":1793394000,"duration":534549,"datasource":"gtr","score":316114.3},
              {"location":"country/NG","location_name":"Nigeria","start":1793800000,"duration":7200,"datasource":"bgp","score":4175},
              {"location":"country/NG","location_name":"Nigeria","start":1793801800,"duration":3600,"datasource":"ping-slash24","score":2000},
              {"location":"country/VE","location_name":"Venezuela","start":1793810000,"duration":1800,"datasource":"bgp","score":296},
              {"location":"region/4401","location_name":"Kherson","start":1793810000,"duration":1800,"datasource":"bgp","score":12},
              {"location":"country/ZZ","location_name":"Nowhere","start":1793810000,"duration":1800,"datasource":"bgp","score":1}]}
            """;

        var outages = InternetOutageService.Parse(json, WorldNewsPlaces.Instance);

        outages.Select(o => o.Id).Should().Equal(["ioda-FI", "ioda-NG", "ioda-VE"], "regions and unknown countries are left out");
        outages.Should().OnlyContain(o => o.Kind == HazardKinds.Internet);
        outages[0].Severity.Should().Be("minor", "Google traffic alone is often noise");
        outages[0].Detail.Should().StartWith("Seen by IODA in Google traffic").And.Contain("lasting 148 h").And.EndWith("Google traffic alone is often noise");
        outages[1].Severity.Should().Be("major", "routing and probing both saw it");
        outages[1].Detail.Should().StartWith("Seen by IODA in routing (BGP), active probing");
        outages[1].Title.Should().Be("Internet outage - " + WorldNewsPlaces.Instance.Country("NG")!.Name, "our own country names, not IODA's");
        outages[1].Url.Should().Be("https://ioda.inetintel.cc.gatech.edu/country/NG");
        outages[1].Latitude.Should().Be(WorldNewsPlaces.Instance.Country("NG")!.Latitude);
        outages[2].Severity.Should().Be("moderate");
        outages[2].Detail.Should().Contain("under an hour");
    }

    [Fact]
    public void Convergence_ShouldNeedThreeKinds_WithTwoPlacedInTheSquare()
    {
        static ConvergenceSignal At(string kind, double lat, double lon) => new(kind, kind, lat, lon, CountryCode: "PH");
        var internet = new ConvergenceSignal(ConvergenceKinds.InternetOutage, "Internet outage - Philippines", 12.8, 121.7, CountryCode: "PH", CountryWide: true);

        SignalConvergence.Find([At(ConvergenceKinds.Cyclone, 14.2, 121.3), At(ConvergenceKinds.Flooding, 14.6, 121.9), At(ConvergenceKinds.News, 14.5, 121.0)], (_, _) => null)
            .Should().ContainSingle().Which.Should().Match<ConvergenceCell>(cell => cell.South == 14 && cell.West == 121 && cell.Kinds.Count == 3
                && cell.Score == 81 && cell.Severity == "moderate");

        SignalConvergence.Find([At(ConvergenceKinds.Cyclone, 14.2, 121.3), At(ConvergenceKinds.Flooding, 14.6, 121.9), internet], (_, _) => null)
            .Should().ContainSingle("two placed kinds and the country's internet outage make three").Which.Signals.Should().Contain(internet);

        SignalConvergence.Find([At(ConvergenceKinds.Cyclone, 14.2, 121.3), internet,
                new ConvergenceSignal(ConvergenceKinds.News, "Country story", 12.8, 121.7, CountryCode: "PH", CountryWide: true)], (_, _) => null)
            .Should().BeEmpty("country-wide signals can't make a square converge on their own");

        SignalConvergence.Find([At(ConvergenceKinds.Flooding, 14.2, 121.3), At(ConvergenceKinds.Flooding, 14.6, 121.9), At(ConvergenceKinds.News, 14.5, 121.0)], (_, _) => null)
            .Should().BeEmpty("a gauge and a GDACS flood are the same kind");

        SignalConvergence.Find([At(ConvergenceKinds.Cyclone, 14.2, 121.3), At(ConvergenceKinds.Flooding, 15.6, 121.9), At(ConvergenceKinds.News, 14.5, 121.0)], (_, _) => null)
            .Should().BeEmpty("the flood is in the next square up");

        var four = SignalConvergence.Find([At(ConvergenceKinds.Cyclone, 14.2, 121.3), At(ConvergenceKinds.Flooding, 14.6, 121.9),
            At(ConvergenceKinds.News, 14.5, 121.0), internet], (_, _) => null).Single();
        four.Severity.Should().Be("major");
        four.Score.Should().Be(SignalConvergence.Score(4, 4)).And.Be(100);
    }

    [Fact]
    public void Signals_ShouldCountOnlyEvents_AndNameTheSquareFromAPlacedStory()
    {
        var hazards = new[]
        {
            new HazardFeature("gdacs-TC-1", HazardKinds.Cyclone, "Tropical Cyclone A", "", 14.2, 121.3, "major", "https://www.gdacs.org/a", null),
            new HazardFeature("gdacs-DR-1", HazardKinds.Drought, "Drought", "", 14.3, 121.3, "moderate", "https://www.gdacs.org/b", null)
        };
        var internet = new[]
        {
            new HazardFeature("ioda-PH", HazardKinds.Internet, "Internet outage - Philippines", "", 12.8, 121.7, "moderate", "https://ioda/PH", null),
            new HazardFeature("ioda-FI", HazardKinds.Internet, "Internet outage - Finland", "", 64, 26, "minor", "https://ioda/FI", null)
        };
        var power = new[]
        {
            new PowerOutageArea("1", "Bibb County", "GA", 12_000, [], 32.8, -83.6, []),
            new PowerOutageArea("2", "Houston County", "GA", 40, [], 32.5, -83.7, [])
        };
        var alerts = new[] { new WeatherAlert("a", "Tornado Warning", "Extreme", "Bibb", "", [[(-83.7, 32.7), (-83.5, 32.9)]]) };
        var manila = WorldNewsPlaces.Instance.Locate("Flooding in Manila, Philippines")!;
        var news = new[] { Story("s1", "Flooding in Manila", manila), Story("s2", "Unrest in Peru", WorldNewsPlaces.Instance.Locate("Peru")!) };

        var signals = SignalConvergenceService.Signals(hazards, internet, power, alerts, news, World);

        signals.Select(s => s.Kind).Should().BeEquivalentTo(
            [ConvergenceKinds.Cyclone, ConvergenceKinds.InternetOutage, ConvergenceKinds.PowerOutage, ConvergenceKinds.SevereWeather, ConvergenceKinds.News, ConvergenceKinds.News],
            "droughts, Google-only outages and small power outages don't count");
        signals.Single(s => s.Kind == ConvergenceKinds.InternetOutage).Should().Match<ConvergenceSignal>(s => s.CountryWide && s.CountryCode == "PH");
        signals.Single(s => s.Title == "Unrest in Peru").CountryWide.Should().BeTrue("Peru is placed only at the country");
        signals.Single(s => s.Kind == ConvergenceKinds.SevereWeather).Latitude.Should().BeApproximately(32.8, 0.001);

        var manilaSquare = new ConvergenceCell(Math.Floor(manila.Latitude), Math.Floor(manila.Longitude), ["cyclone", "news"], 75, "moderate",
            signals.Where(s => s.CountryCode == "PH").ToList());
        SignalConvergenceService.Label(manilaSquare).Should().Be(manila.Label);
        SignalConvergenceService.Label(new ConvergenceCell(14, 121, ["cyclone"], 50, "moderate", [new ConvergenceSignal("cyclone", "Tropical Cyclone A", 14.65, 121.05)]))
            .Should().Be("Philippines (14°N 121°E)");
    }

    [Fact]
    public void Briefing_ShouldNameTheStrongestPlacesWhereSignalsMeet()
    {
        var facts = new BriefingFacts("Overwatch view", DateTimeOffset.UtcNow, [new BriefingFact(1, "hazard", "M5.1 - offshore")]);
        var cells = Enumerable.Range(0, 3).Select(i => new ConvergenceCell(14 + i, 121, ["cyclone", "flooding", "news"], 90 - i, "major", [], $"Place {i}")).ToList();

        var briefed = OverwatchBriefingService.AddConvergence(facts, cells);

        briefed.Facts.Should().HaveCount(1 + OverwatchBriefingService.MaxConvergences);
        briefed.Facts[1].Should().Match<BriefingFact>(f => f.Number == 2 && f.Kind == "convergence" && f.Latitude == 14.5
            && f.Text == "Several kinds of signal meet near Place 0: cyclone, flooding, news (0 items in the last day)");
        OverwatchBriefing.PlainText(briefed).Should().Contain("2 places where several signals meet");
    }

    private static WorldNewsMapSignal Story(string id, string title, WorldNewsLocation location) =>
        new(id, title, title, location, 3, 5, 5, DateTimeOffset.UtcNow, null, [new WorldNewsMapSource("Outlet", title, $"https://news.example/{id}")]);
}
