using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Application.CameraIntel;
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
        using var temp = new TempStore();
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
        using var temp = new TempStore();
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
        using var temp = new TempStore();
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

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // A throwaway World News database with situations seeded as a read would leave them.
    private sealed class TempStore : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"wn-map-{Guid.NewGuid():N}.db");

        public TempStore() => Store = new WorldNewsStore(_path);

        public WorldNewsStore Store { get; }

        // Two events of three outlets each, linked into one situation.
        public void SeedSituation(string id, string firstTitle, string secondTitle)
        {
            SeedEvent(id, $"{id}-1", firstTitle, ["Outlet 0", "Outlet 1", "Outlet 2"], Now.AddHours(-4));
            SeedEvent(id, $"{id}-2", secondTitle, ["Outlet 0", "Outlet 1", "Outlet 2"], Now.AddHours(-4));
        }

        public void SeedEvent(string situationId, string eventId, string title, string[] outlets, DateTimeOffset seen)
        {
            var situation = new NewsSituation { Id = situationId, Sum = [1f, 0f], LastSeen = Now.AddHours(-1) };
            var reports = outlets.Select((outlet, i) => new WorldArticle($"{eventId}-{i}", $"https://example.com/{eventId}/{i}",
                title, outlet, "English", "Somewhere", seen, "test", null)).ToArray();
            Store.AddArticles(reports);
            Store.SaveClustering(new ClusterResult(reports.Select(r => (r.Id, eventId)).ToList(),
            [
                new NewsEvent
                {
                    Id = eventId, Title = title, TitleLanguage = "English", Sum = [reports.Length, 0f], ArticleCount = reports.Length,
                    FirstSeen = seen.AddHours(-1), LastSeen = Now.AddHours(-1)
                }
            ], 1));
            Store.SaveLinks([new SituationLink(eventId, situation)]);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(file);
        }
    }
}
