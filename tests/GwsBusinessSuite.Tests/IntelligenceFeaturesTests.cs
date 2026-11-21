using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// Phase 2 of the intelligence plan (20-11-26): the new features in Media Watch, World News and
// Civic Watch, tested on their rules (the services' database parts are in MediaWatchTests and
// CivicWatchTests).
public sealed class IntelligenceFeaturesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    // ---- Media Watch ----

    [Fact]
    public void ReleaseRadar_ShouldOnlyFollowPackagesTheRepoReferences()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var projects = string.Join('\n', Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var (name, package, repo) in ReleaseRadar.Sources.Where(s => s.Package is not null))
            projects.Should().Contain($"Include=\"{package}\"", $"{name} is on the radar because GWS uses {package}");
        ReleaseRadar.Sources.Select(s => s.Repo).Should().OnlyHaveUniqueItems().And.OnlyContain(r => r.Count(c => c == '/') == 1);
        var merged = ReleaseRadar.MergeFeeds("https://example.com/feed");
        merged.Split('\n').Should().HaveCount(ReleaseRadar.Sources.Count + 1).And.Contain("https://github.com/dotnet/efcore/releases.atom");
        ReleaseRadar.MergeFeeds(merged).Should().Be(merged);
    }

    [Theory]
    [InlineData("jane@example.com (Jane Doe)", "Jane Doe")]
    [InlineData("By  Jane   Doe", "Jane Doe")]
    [InlineData("jane@example.com", null)]
    [InlineData("Staff", null)]
    [InlineData("https://example.com/author", null)]
    [InlineData("  ", null)]
    public void Authors_ShouldBeCleanedForFollowing(string raw, string? expected) => NewsAuthors.Clean(raw).Should().Be(expected);

    [Fact]
    public void Library_ShouldNeedEveryWord_AndQuoteAroundTheFirstMatch()
    {
        ReadingLibrary.Matches("retries jitter", "Retries need", "a jitter budget").Should().BeTrue();
        ReadingLibrary.Matches("retry kafka", "Retries need jitter").Should().BeFalse();
        ReadingLibrary.Snippet(new string('a', 300) + " jitter " + new string('b', 300), "jitter")!.Should().StartWith("…").And.Contain("jitter").And.EndWith("…");
    }

    [Fact]
    public void Resurface_ShouldPickOldHighlights_ThreeAWeek_AndTheSameAllWeek()
    {
        var clips = Enumerable.Range(1, 5).Select(i => ($"Clip {i}", $"Intro\n> Highlight number {i} worth keeping around\nmore", Now.AddDays(-10 - i))).ToList();
        clips.Add(("Fresh", "> A highlight from this very week, too new", Now.AddDays(-1)));
        var picks = ReadingLibrary.Resurface(clips, Now);
        picks.Should().HaveCount(3).And.OnlyContain(p => p.Quote.StartsWith("Highlight number", StringComparison.Ordinal));
        ReadingLibrary.Resurface(clips, Now.AddHours(1)).Should().Equal(picks, "a rebuild the same week agrees");
        ReadingLibrary.QuoteLines("> one long enough quote\n> carried on\ntext\n> short").Should().Equal("one long enough quote carried on");
    }

    [Fact]
    public void Answer_ShouldKeepOnlyAQuoteThatIsReallyInTheArticle()
    {
        const string article = "The team moved to SQLite.  It cut latency by half for reads.";
        var real = ArticleReaderService.ParseAnswer("ANSWER: Latency halved.\nQUOTE: \"It cut latency by half for reads.\"", article);
        real.Should().BeEquivalentTo(new ReaderAnswer("Latency halved.", "It cut latency by half for reads.", null));
        ArticleReaderService.ParseAnswer("ANSWER: Yes.\nQUOTE: It doubled throughput overnight.", article).Quote.Should().BeNull("made-up quotes are dropped");
        ArticleReaderService.ParseAnswer("", article).Problem.Should().NotBeNull();
    }

    // ---- World News ----

    private static WorldArticle Report(string outlet, string country, DateTimeOffset at, string title = "Quake hits", string language = "English") =>
        new(Guid.NewGuid().ToString("N"), $"https://{outlet}.example/{Guid.NewGuid():N}", title, outlet, language, country, at, "feed");

    [Fact]
    public void Spread_ShouldCountWhoJoinedEachHour()
    {
        var hours = StorySpread.Hourly([
            Report("a", "Japan", Now.AddMinutes(5)), Report("b", "Japan", Now.AddMinutes(40)),
            Report("a", "Japan", Now.AddHours(1).AddMinutes(1)),
            Report("c", "France", Now.AddHours(3)),
        ]);
        hours.Select(h => (h.NewOutlets.Count, h.Outlets, h.Countries)).Should().Equal((2, 2, 1), (1, 3, 2));
        hours[1].NewCountries.Should().Equal("France");
    }

    // 21-11-26: the replay section shared .story-spread with the coverage head's 9rem sparkline box and
    // rendered one letter per line.
    [Fact]
    public void Spread_ShouldHaveItsOwnSection_NotTheSparklineBox()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var view = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/WorldNewsStoryView.razor"));
        view.Should().Contain("<section class=\"story-part story-hourly-spread\" aria-labelledby=\"story-h-spread\">")
            .And.NotContain("class=\"story-part story-spread\"");
    }

    [Fact]
    public void CatchUp_ShouldListTheNewReportsInOrder()
    {
        var prompt = CatchUp.Prompt("Quake hits Japan", Now, [Report("later", "Japan", Now.AddHours(2), "Toll rises"), Report("first", "Japan", Now.AddHours(1), "Aftershock")],
            at => at.ToString("HH:mm"));
        prompt.IndexOf("first", StringComparison.Ordinal).Should().BeLessThan(prompt.IndexOf("later", StringComparison.Ordinal));
        prompt.Should().Contain("Last read: 12:00");
    }

    [Fact]
    public void MorningBriefing_ShouldBeDueOnceEachMorningFromSeven()
    {
        var morning = new DateTimeOffset(2026, 11, 20, 7, 30, 0, TimeSpan.Zero);
        MorningBriefing.Due(null, morning.AddHours(-1)).Should().BeFalse("before seven");
        MorningBriefing.Due(null, morning).Should().BeTrue();
        MorningBriefing.Due(morning.AddMinutes(-5), morning).Should().BeFalse("already built this morning");
        MorningBriefing.Due(morning.AddDays(-1), morning).Should().BeTrue();
    }

    [Fact]
    public void BriefingAndDossier_ShouldLinkEachStory()
    {
        var story = new EventOverview("e1", "Flood [update]", 9, 6, ["Spain"], ["Spanish", "English"], Now, Now, "Floods in Valencia");
        var md = MorningBriefing.Markdown(Now, [new MorningBriefing.Lead(story, "Rivers burst.")], [("Valencia", [story])], [], WorldNewsService.StoryLink);
        md.Should().Contain("[Floods in Valencia](/admin/world-news/events/e1)").And.Contain("Rivers burst.").And.Contain("**Valencia**");
        WatchDossier.Markdown("Valencia", [story with { Headline = null }], Now, WorldNewsService.StoryLink).Should().Contain("Flood \\[update\\]");
        WatchDossier.KeyFor("Valencia").Should().Be(WatchDossier.KeyFor(" valencia "));
    }

    [Fact]
    public void EntitySearch_ShouldTakeTheBestMatch()
    {
        var facts = WorldNewsService.ParseEntitySearch("""{"search":[{"id":"Q95","label":"Google","description":"American technology company"}]}""");
        facts.Should().BeEquivalentTo(new { Label = "Google", Description = "American technology company", WikidataUrl = "https://www.wikidata.org/wiki/Q95" });
        WorldNewsService.ParseEntitySearch("""{"search":[]}""").Should().BeNull();
    }

    [Fact]
    public void BriefingSubscribers_ShouldBeKeptInTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"worldnews-features-{Guid.NewGuid():N}.db");
        try
        {
            var store = new WorldNewsStore(path);
            store.WantsBriefing("Grant").Should().BeFalse();
            store.SetBriefing("Grant", true, Now);
            store.WantsBriefing("grant").Should().BeTrue();
            store.BriefingSubscribers().Should().ContainSingle().Which.LastBuilt.Should().BeNull();
            store.MarkBriefingBuilt("grant", Now);
            store.BriefingSubscribers().Single().LastBuilt.Should().Be(Now);
            store.StoriesMatching("nothing here", Now.AddDays(-1), 5).Should().BeEmpty();
            store.FrontPagesSince(Now.AddDays(-1), FrontPages.Countries).Should().BeEmpty();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    // ---- Civic Watch ----

    [Fact]
    public void Calendar_ShouldBeValidICalendar_EscapedAndFolded()
    {
        var ics = CivicCalendar.Build([
            new CivicCalendarItem("a", new DateOnly(2026, 12, 1), "Commission; budget, hearing", "https://example.com/m", new string('x', 200)),
            new CivicCalendarItem("a", new DateOnly(2026, 12, 1), "Duplicate", null, null),
        ], Now);
        ics.Should().StartWith("BEGIN:VCALENDAR\r\n").And.EndWith("END:VCALENDAR\r\n");
        ics.Should().Contain("DTSTART;VALUE=DATE:20261201").And.Contain("DTEND;VALUE=DATE:20261202").And.Contain("SUMMARY:Commission\\; budget\\, hearing");
        ics.Split("\r\n").Should().OnlyContain(line => Encoding.UTF8.GetByteCount(line) <= 75);
        ics.Split("BEGIN:VEVENT").Should().HaveCount(2, "one event per id");
        CivicCalendar.NewToken().Should().HaveLength(48).And.MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void Scorecards_ShouldCountMissedVotesAndPartyLine()
    {
        CivicScorecards.PartyMajority(["Yea", "Yea", "Nay"]).Should().Be("yea");
        CivicScorecards.PartyMajority(["Yea", "Nay"]).Should().BeNull("a tie has no majority");
        var facts = new[]
        {
            new CivicMemberVoteFact("M1", "r1", "Yea", "yea", Now.AddDays(-9)),
            new CivicMemberVoteFact("M1", "r2", "Nay", "yea", Now.AddDays(-5)),
            new CivicMemberVoteFact("M1", "r3", "Not Voting", "nay", Now.AddDays(-1)),
            new CivicMemberVoteFact("M2", "r1", "Yea", null, Now),
        };
        var card = CivicScorecards.For("M1", "Ann", "House", facts);
        (card.RollCalls, card.Missed, card.WithParty, card.PartyLineVotes).Should().Be((3, 1, 1, 2));
        card.Since.Should().Be(Now.AddDays(-9));
        CivicScorecards.For("M2", "Bo", "Senate", facts).WithPartyShare.Should().BeNull("state votes carry no party");
    }

    [Fact]
    public void SchoolBoard_ShouldReadTheDistrictsListing()
    {
        const string html = """
            <tr>
            <td><span id='x' title='11/10/2026 - 01:00 PM'>11/10/2026 - 01:00 PM</span></td>
            <td><a onclick='javascript:return ViewMeeting("4089","140158","1","0","P",event);' href='javascript:void(0);'>Regular Monthly Board Meeting </a> </td>
            <td><a onclick='javascript:return ViewMinutes("4089","140158","UNPUBLISHED","0","P","0",event);'>x</a></td>
            </tr>
            <tr>
            <td><span title='10/06/2026 - 01:00 PM'>10/06/2026 - 01:00 PM</span></td>
            <td><a onclick='javascript:return ViewMeeting("4089","139001","1","0","P",event);'>Regular Monthly &amp; Budget Meeting</a></td>
            <td><a onclick='javascript:return ViewMinutes("4089","139001","PUBLISHED","0","P","0",event);'>x</a></td>
            </tr>
            """;
        var meetings = CivicWatchService.ParseSchoolBoardMeetings(html);
        meetings.Should().HaveCount(2);
        meetings[0].Should().BeEquivalentTo(new { Title = "Regular Monthly Board Meeting", MinutesPublished = false });
        meetings[0].Url.Should().EndWith("MID=140158");
        meetings[0].StartsAt.Hour.Should().Be(13);
        meetings[1].Should().BeEquivalentTo(new { Title = "Regular Monthly & Budget Meeting", MinutesPublished = true });
    }

    [Fact]
    public void WeeklyCivicPage_ShouldCoverEachSection()
    {
        var md = CivicWeeklyPage.Markdown(new CivicWeeklyPage.Input(Now,
            [new CivicChangedItem("community", "Notice", "Road closed", "https://example.com/n", Now)],
            [new CivicCalendarItem("e", new DateOnly(2026, 11, 25), "Commission meeting", null, null)],
            [new CivicScorecard("M1", "Ann", "House", 10, 1, 8, 9, Now)],
            [new CivicFollowedItem("Highway 96", [new CivicItemMention(new DateOnly(2026, 11, 3), "Agenda", "t", "https://example.com/a.pdf", "s")])],
            []));
        md.Should().Contain("## What changed this week").And.Contain("[Road closed](https://example.com/n)")
            .And.Contain("25-11-26 · Commission meeting").And.Contain("missed 10%").And.Contain("**Highway 96**: last in the agenda of 03-11-26");
    }

    [Fact]
    public void PublicCommentPrompt_ShouldCarryTheDocumentAndPosition()
    {
        var doc = new FederalRegisterDocument("2026-1234", "Broadband rule", "Proposed Rule", "https://www.federalregister.gov/d/2026-1234",
            new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 15), "FCC", "Sets speeds.", "broadband");
        PublicComment.Prompt(doc, " Rural homes need this. ").Should().Contain("Broadband rule").And.Contain("15-12-26").And.EndWith("Rural homes need this.");
    }
}
