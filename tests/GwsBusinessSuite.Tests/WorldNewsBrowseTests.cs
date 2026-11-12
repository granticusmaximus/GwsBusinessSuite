using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News design pass (12-11-26): tabs in the reader's words, the page's state in its address,
// and the row facts (kind, place, time, coverage) that make a list scannable.
public sealed class WorldNewsBrowseTests
{
    [Theory]
    [InlineData(null, null, null, WorldNewsTab.Today)]
    [InlineData("latest", null, null, WorldNewsTab.Latest)]
    [InlineData("developing", null, null, WorldNewsTab.Developing)]
    [InlineData("reports", null, null, WorldNewsTab.Reports)]
    [InlineData("events", null, null, WorldNewsTab.Latest)]
    [InlineData("situations", null, null, WorldNewsTab.Developing)]
    [InlineData("world", null, null, WorldNewsTab.Today)]
    [InlineData(null, "e1", null, WorldNewsTab.Latest)]
    [InlineData(null, null, "s1", WorldNewsTab.Developing)]
    [InlineData("nonsense", null, null, WorldNewsTab.Today)]
    public void ParseTab_ShouldKeepOldLinksWorking(string? key, string? eventId, string? situationId, WorldNewsTab expected) =>
        WorldNewsBrowse.ParseTab(key, eventId, situationId).Should().Be(expected);

    [Fact]
    public void Url_ShouldHoldOnlyWhatDiffersFromTheDefaults_AndRoundTrip()
    {
        WorldNewsBrowse.Url(new WorldNewsBrowseState()).Should().Be("admin/world-news");
        WorldNewsBrowse.Url(new WorldNewsBrowseState(RangeHours: 3)).Should().Be("admin/world-news", "3 hours is Today's default");
        WorldNewsBrowse.Url(new WorldNewsBrowseState(WorldNewsTab.Latest, RangeHours: 24, EventId: "abc")).Should().Be("admin/world-news?view=latest&event=abc");

        var url = WorldNewsBrowse.Url(new WorldNewsBrowseState(WorldNewsTab.Developing, " flood & fire ", 72, SituationKinds.Disaster,
            "Brazil", "Japan", "Spanish", "", null, "s 1"));
        url.Should().Be("admin/world-news?view=developing&q=flood%20%26%20fire&range=72&kind=disaster&where=Brazil&country=Japan&language=Spanish&situation=s%201");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("https://x/" + url).Query);
        WorldNewsBrowse.ParseTab(query["view"]).Should().Be(WorldNewsTab.Developing);
        query["q"].Should().Be("flood & fire");
        query["situation"].Should().Be("s 1");
    }

    [Fact]
    public void RangeFor_ShouldOnlyOfferEachTabsOwnRanges()
    {
        WorldNewsBrowse.RangeFor(WorldNewsTab.Today, 24).Should().Be(24);
        WorldNewsBrowse.RangeFor(WorldNewsTab.Today, 168).Should().Be(3);
        WorldNewsBrowse.RangeFor(WorldNewsTab.Latest, 3).Should().Be(24);
        WorldNewsBrowse.RangeFor(WorldNewsTab.Latest, 720).Should().Be(24, "a month is the archive's alone");
        WorldNewsBrowse.RangeFor(WorldNewsTab.Reports, 720).Should().Be(720);
    }

    [Fact]
    public void TimeLabel_ShouldShowTodayAsATime_AndEarlierDaysInFull()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
        WorldNewsBrowse.TimeLabel(now.AddHours(-2), now, zone).Should().Be("14:00");
        WorldNewsBrowse.TimeLabel(new DateTimeOffset(2026, 11, 11, 23, 5, 0, TimeSpan.Zero), now, zone).Should().Be("11-11-26 23:05");
    }

    [Fact]
    public void Seen_AndReach_ShouldReadNaturally_AndKeepSmallStoriesVisible()
    {
        WorldNewsBrowse.Seen(1, 1).Should().Be("Seen by 1 outlet");
        WorldNewsBrowse.Seen(14, 6).Should().Be("Seen by 14 outlets in 6 languages");
        WorldNewsBrowse.ReachPercent(64, 64).Should().Be(100);
        WorldNewsBrowse.ReachPercent(16, 64).Should().Be(50, "square-root scale");
        WorldNewsBrowse.ReachPercent(0, 64).Should().Be(6);
        WorldNewsBrowse.ReachPercent(5, 0).Should().Be(6);
    }

    [Fact]
    public void Facts_ShouldNameTheKindAndThePlaceFromTheHeadline()
    {
        var e = new EventOverview("e1", "Sismo sacude Manila", 9, 7, ["Philippines"], ["Spanish"],
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, Headline: "Earthquake shakes Manila, killing dozens");

        var facts = WorldNewsBrowse.Facts(e);

        facts.Kind.Should().Be(SituationKinds.Disaster);
        facts.KindLabel.Should().Be(SituationKinds.Label(SituationKinds.Disaster));
        facts.Place.Should().Be("Manila, Philippines");
        facts.PlaceCountry.Should().Be("Philippines");
        SituationKinds.All.Where(kind => kind != SituationKinds.Other).Select(WorldNewsBrowse.ChipLabel)
            .Should().OnlyContain(label => !label.Contains(' '), "each chip is one word");
    }

    [Fact]
    public void Matches_ShouldApplyWordsKindWhereAndSources()
    {
        var facts = new WorldNewsRowFacts(SituationKinds.Disaster, "Disaster", "Manila, Philippines", "Philippines");
        IReadOnlyList<string> countries = ["Japan", "Philippines"];
        IReadOnlyList<string> languages = ["English", "Japanese"];
        bool Matches(WorldNewsBrowseState state) => WorldNewsBrowse.Matches("Earthquake shakes Manila", null, facts, countries, languages, state);

        Matches(new()).Should().BeTrue();
        Matches(new(Query: "manila")).Should().BeTrue();
        Matches(new(Query: "election")).Should().BeFalse();
        Matches(new(Kind: SituationKinds.Disaster)).Should().BeTrue();
        Matches(new(Kind: SituationKinds.Conflict)).Should().BeFalse();
        Matches(new(Where: "philippines")).Should().BeTrue();
        Matches(new(Where: "Japan")).Should().BeFalse("Where is the country the story is in, not who reported it");
        Matches(new(Country: "Japan")).Should().BeTrue("reported from is the outlets' country");
        Matches(new(Language: "French")).Should().BeFalse();
    }
}
