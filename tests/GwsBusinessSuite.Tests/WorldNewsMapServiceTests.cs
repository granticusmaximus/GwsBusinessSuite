using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RouteWatch;
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
        using var temp = new WorldNewsTestStore();
        var map = new WorldNewsMapService(temp.Store);
        var since = Now - WorldNewsMapService.Window;
        SituationOverview Situation(string title, int outlets, DateTimeOffset lastSeen) =>
            new("s1", title, title, 2, 9, outlets, ["gb"], ["en"], lastSeen.AddDays(-1), lastSeen);

        map.Place(Situation("Japan earthquake aftershocks", 5, Now), since)!.Country.Should().Be("Japan");
        map.Place(Situation("Japan earthquake aftershocks", 2, Now), since)
            .Should().BeNull("two outlets is below the layer's corroboration bar");
        map.Place(Situation("Japan earthquake aftershocks", 5, Now.AddDays(-2)), since)
            .Should().BeNull("the layer only looks back a day");
        map.Place(Situation("Markets rally after rate decision", 5, Now), since).Should().BeNull();
    }

    [Fact]
    public void Corrections_ShouldPlaceHideAndRestoreASituation()
    {
        using var temp = new WorldNewsTestStore();
        temp.SeedSituation("ebola", "Ebola cases exceed 10,000 in Congo", "Health workers race to contain the outbreak");
        var map = new WorldNewsMapService(temp.Store);
        var world = new BoundingBox(90, -90, 180, -180);
        var since = Now - WorldNewsMapService.Window;

        map.Signals(world, since).Should().BeEmpty("\"Congo\" alone could be either Congo");

        map.Correct("ebola", "CD", "Narnia", "grant", Now).Should().Contain("No city called \"Narnia\"");
        map.Correct("ebola", "CD", "kinshasa", "grant", Now).Should().BeNull();
        var signal = map.Signals(world, since).Should().ContainSingle().Subject;
        signal.Location.Should().Match<WorldNewsLocation>(l => l.City == "Kinshasa" && l.CountryCode == "CD"
            && l.Precision == WorldNewsPrecision.Corrected);
        map.Correction("ebola")!.UpdatedBy.Should().Be("grant");

        map.Hide("ebola", "grant", Now);
        map.Signals(world, since).Should().BeEmpty();
        map.Signal("ebola", since).Should().BeNull("a hidden situation is off the globe everywhere");

        map.ClearCorrection("ebola");
        map.Correction("ebola").Should().BeNull();
        map.Signals(world, since).Should().BeEmpty("back to what the headlines name");
    }

    [Fact]
    public void Signals_ShouldShowTheChosenKinds_AndFlagSurgingStories()
    {
        using var temp = new WorldNewsTestStore();
        temp.SeedSituation("crash", "Two trains collide in Spain", "Spanish rail services resume");
        temp.SeedSituation("final", "Spain wins the cup final", "Spanish fans celebrate in Madrid");
        temp.Store.SaveKinds([("crash", SituationKinds.Transport), ("final", SituationKinds.Sport)]);
        var map = new WorldNewsMapService(temp.Store);
        var world = new BoundingBox(90, -90, 180, -180);
        var since = Now.AddDays(-1);

        map.Signals(world, since, SituationKinds.Operational).Select(s => s.SituationId).Should().Equal("crash");
        map.Signals(world, since).Should().HaveCount(2, "no filter shows every kind");
        map.Signals(world, since, [SituationKinds.Sport]).Single().Kind.Should().Be(SituationKinds.Sport);

        map.Signals(world, since, SituationKinds.Operational).Single().Surge.Should().BeNull("three outlets for hours isn't a surge");
        temp.SeedEvent("crash", "crash-3", "Death toll rises in Spain train crash",
            ["Outlet 3", "Outlet 4", "Outlet 5", "Outlet 6", "Outlet 7"], Now.AddMinutes(-30));
        var surging = map.Signals(world, since, SituationKinds.Operational).Single();
        surging.Surge.Should().Be("Surging: coverage went from 3 to 8 outlets in the last 2 hours.");
        surging.Active.Should().BeTrue();
    }

    [Fact]
    public void AlongRoute_ShouldFindNearbyStories_AndCountryWideStrikesOrUnrest()
    {
        using var temp = new WorldNewsTestStore();
        temp.SeedSituation("toledo", "Trains collide near Toledo, Spain", "Toledo, Spain crash inquiry");
        temp.SeedSituation("strike", "Spain rail strike halts trains", "Spanish rail strike enters day two");
        temp.SeedSituation("floods", "Spain floods leave thousands homeless", "Spanish floods recede");
        temp.SeedSituation("barcelona", "Protest in Barcelona, Spain turns violent", "Barcelona, Spain clashes continue");
        temp.Store.SaveKinds([("toledo", SituationKinds.Transport), ("strike", SituationKinds.Transport),
            ("floods", SituationKinds.Disaster), ("barcelona", SituationKinds.Conflict)]);
        var map = new WorldNewsMapService(temp.Store);
        // Madrid to Toledo, roughly along the A-42.
        RoutePoint[] path = [new(40.42, -3.70), new(40.24, -3.76), new(40.05, -3.92), new(39.86, -4.02)];

        var items = map.AlongRoute(path, Now.AddDays(-3));

        items.Select(i => (i.Signal.SituationId, i.Reason)).Should().Equal(
            ("toledo", "on the route"),
            ("strike", "in Spain, which the route crosses"));
    }

    [Fact]
    public void CountryTotalsCoverageAndTimeline_ShouldDescribeWhereStoriesAreAndWhoReportsThem()
    {
        using var temp = new WorldNewsTestStore();
        temp.SeedEvent("quake", "quake-1", "Quake shakes Tokyo, Japan", ["NHK", "Kyodo", "BBC"], Now.AddHours(-6));
        temp.SeedEvent("quake", "quake-2", "Aftershocks hit Sendai, Japan", ["NHK", "Reuters", "CNN", "DW"], Now.AddHours(-2));
        temp.SeedSituation("crash", "Trains collide near Toledo, Spain", "Spain rail inquiry opens");
        temp.Store.SaveKinds([("quake", SituationKinds.Disaster), ("crash", SituationKinds.Transport)]);
        var map = new WorldNewsMapService(temp.Store);

        map.CountryTotals(Now.AddDays(-1), SituationKinds.Operational).Select(t => (t.Code, t.Outlets)).Should().Equal(("JP", 6), ("ES", 3));

        map.Coverage("quake").Should().ContainSingle("every seeded report is from a UK edition")
            .Which.Should().Match<WorldNewsCoverage>(c => c.Country == "United Kingdom" && c.Outlets == 6);

        var japan = map.Signal("quake", Now.AddDays(-1))!.Location;
        map.Timeline("quake", japan).Select(step => step.Location.City).Should().Equal("Tokyo", "Sendai");
    }

    [Fact]
    public void Cache_ShouldServeGlobeMovesFromMemory_UntilACorrectionClearsIt()
    {
        using var temp = new WorldNewsTestStore();
        temp.SeedSituation("ebola", "Ebola cases exceed 10,000 in Congo", "Health workers race to contain the outbreak");
        var cache = new WorldNewsMapCache(TimeProvider.System);
        var map = new WorldNewsMapService(temp.Store, cache);
        var world = new BoundingBox(90, -90, 180, -180);
        var since = Now.AddDays(-1);

        map.Signals(world, since).Should().BeEmpty();
        temp.SeedSituation("quake", "Earthquake shakes Tokyo, Japan", "Tokyo aftershocks");
        map.Signals(world, since).Should().BeEmpty("the same minute's list comes from memory");

        map.Correct("ebola", "CD", "Kinshasa", "grant", Now);
        map.Signals(world, since).Select(s => s.SituationId).Should().BeEquivalentTo(["ebola", "quake"], "a correction clears the cache");
    }

    private static readonly DateTimeOffset Now = WorldNewsTestStore.Now;
}
