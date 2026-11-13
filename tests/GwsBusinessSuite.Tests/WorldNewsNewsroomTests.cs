using FluentAssertions;
using GwsBusinessSuite.Web;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GwsBusinessSuite.Tests;

// World News trust plan, Phase B first slice (13-11-26): evidence board, sensor checks, the desk's
// checklist, the write-up draft, the budget and the tip line.
public sealed class WorldNewsNewsroomTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static EvidenceSignal Signal(string source, string kind, string title, double km) =>
        new(source, kind, title, 0, 0, Now, "https://example.org", km);

    [Fact]
    public void EvidenceBoard_ShouldKeepNearbyRecentSignalsInTimeOrder()
    {
        var signals = new[]
        {
            new PrimarySignal("USGS", "earthquake", "near, later", 35.7, 139.7, Now.AddHours(-1), "a"),
            new PrimarySignal("USGS", "earthquake", "near, earlier", 35.8, 139.8, Now.AddHours(-5), "b"),
            new PrimarySignal("IODA", "internet", "undated outage", 35.6, 139.6, null, "c"),
            new PrimarySignal("USGS", "earthquake", "too far", -33.9, 151.2, Now.AddHours(-2), "d"),
            new PrimarySignal("USGS", "earthquake", "too old", 35.7, 139.7, Now.AddDays(-5), "e"),
        };

        var near = EvidenceBoards.Near(signals, 35.68, 139.69, Now.AddHours(-6), Now, EvidenceBoards.CityKilometres);

        near.Select(s => s.Title).Should().Equal("near, earlier", "near, later", "undated outage");
        near.Should().OnlyContain(s => s.Kilometres < 50);
    }

    [Fact]
    public void SensorChecks_ShouldSupportAClaimASensorSawAndNeverCallSilenceAContradiction()
    {
        var nearby = new[] { Signal("IODA", "internet", "Iran national connectivity drop", 120) };

        var checks = SensorChecks.Check("Internet shutdown and wildfires reported across Iran", nearby);

        checks.Should().HaveCount(2);
        checks.Single(c => c.Claim == "Internet shutdown").Verdict.Should().Be(SensorVerdict.Supported);
        checks.Single(c => c.Claim == "Internet shutdown").Detail.Should().Contain("120 km");
        var fire = checks.Single(c => c.Claim == "Fire");
        fire.Verdict.Should().Be(SensorVerdict.NoSignal);
        fire.Detail.Should().Contain("doesn't rule it out");
    }

    [Fact]
    public void SensorChecks_ShouldMakeNoClaimForAHeadlineWithoutAPhysicalEvent() =>
        SensorChecks.Check("Parliament passes budget after long debate", [Signal("USGS", "earthquake", "M5", 10)]).Should().BeEmpty();

    [Fact]
    public void Board_ShouldShowWhatTheHeadlineIsAboutAndKeepBackgroundFiresOff()
    {
        var nearby = new[]
        {
            Signal("NASA FIRMS", "hotspot", "3 detections", 400),
            Signal("USGS", "earthquake", "M5.9", 80),
            Signal("IODA", "internet", "Connectivity drop", 200),
        };

        SensorChecks.Relevant("Quake shakes the capital", nearby).Select(s => s.Kind).Should().Equal("earthquake");
        SensorChecks.Relevant("Wildfires spread near the coast", nearby).Select(s => s.Kind).Should().Equal("hotspot");
        SensorChecks.Relevant("President re-elected in runoff", nearby).Select(s => s.Kind).Should().Equal("earthquake", "internet");
    }

    [Fact]
    public void Checklist_ShouldBeCompleteOnlyWhenEveryItemIsTickedWithEvidence()
    {
        var all = DeskChecklist.Items.Select(i => new DeskCheck("e1", i.Key, true, "seen it", "grant", Now)).ToList();
        DeskChecklist.IsComplete(all).Should().BeTrue();

        DeskChecklist.IsComplete(all.Skip(1)).Should().BeFalse();
        DeskChecklist.IsComplete(all.Select((c, i) => i == 0 ? c with { Evidence = " " } : c)).Should().BeFalse();
        DeskChecklist.IsComplete(all.Select((c, i) => i == 0 ? c with { Done = false } : c)).Should().BeFalse();
    }

    [Fact]
    public void Checklist_ShouldSuggestEvidenceTheSystemAlreadyHas()
    {
        var first = new WorldArticle("a1", "https://kyodo.example/1", "Quake hits Tokyo", "Kyodo", "English", "Japan", Now, "test", null);
        var board = new EvidenceBoard([Signal("USGS", "earthquake", "M6.1 near Tokyo", 20)], [], "Tokyo, Japan");

        DeskChecklist.Suggest("origin", null, board, first).Should().Be("First report: Kyodo, https://kyodo.example/1");
        DeskChecklist.Suggest("official-record", null, board, first).Should().Be("USGS: M6.1 near Tokyo");
        DeskChecklist.Suggest("location", null, board, first).Should().Be("Tokyo, Japan");
        DeskChecklist.Suggest("media", null, board, first).Should().BeNull();
    }

    [Fact]
    public void Draft_ShouldShowTheChecklistSourcesAndRules()
    {
        var report = new WorldArticle("a1", "https://kyodo.example/1", "Quake hits Tokyo", "Kyodo", "English", "Japan", Now, "test", null);
        var story = new StoryDocument(
            new EventOverview("e1", "Quake hits Tokyo", 1, 1, ["Japan"], ["English"], Now, Now),
            null, null, [], [], [], new Dictionary<string, string>(), null, FirstReport: report);
        var checks = new[] { new DeskCheck("e1", "origin", true, "Kyodo saw it", "grant", Now) };
        var board = new EvidenceBoard([], [new SensorCheck("Earthquake", SensorVerdict.Supported, "USGS: M6.1 (20 km away)")], "Tokyo, Japan");

        var md = StoryDraft.Markdown(story, checks, board, at => at.ToString("dd-MM-yy HH:mm"));

        md.Should().Contain("## How we verified this");
        md.Should().Contain("- [x] Origin found: Kyodo saw it (grant, 13-11-26 09:00)");
        md.Should().Contain("- [ ] Second independent source: not checked yet");
        md.Should().Contain("- Earthquake: supported. USGS: M6.1 (20 km away)");
        md.Should().Contain("First reported by Kyodo, 13-11-26 09:00");
        md.Should().Contain(StoryConfidenceRules.RulesVersion);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("short", null, false)]
    [InlineData("Saw smoke over the port at 14:00 today.", null, true)]
    [InlineData("Saw smoke over the port at 14:00 today.", "https://example.org/photo", true)]
    [InlineData("Saw smoke over the port at 14:00 today.", "javascript:alert(1)", false)]
    [InlineData("Saw smoke over the port at 14:00 today.", "ftp://example.org/x", false)]
    public void TipRules_ShouldAcceptOnlyASentenceAndAWebLink(string? message, string? url, bool ok) =>
        (TipRules.Problem(message, url) is null).Should().Be(ok);

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("https://gwsapp.net/admin/world-news?event=abc123&x=1", "abc123")]
    [InlineData("admin/world-news?event=abc%20123#top", "abc 123")]
    [InlineData("  ", null)]
    [InlineData("admin/world-news?event=", null)]
    public void TipRules_ShouldReadAStoryFromAnIdOrALink(string text, string? expected) =>
        TipRules.StoryIdFrom(text).Should().Be(expected);

    [Fact]
    public void Store_ShouldKeepDeskChecksBudgetAndTips()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        test.SeedEvent("s1", "e1", "Quake hits Tokyo", ["A", "B"], Now);

        store.SetDeskCheck("e1", "origin", true, "Kyodo", "grant", Now);
        store.SetDeskCheck("e1", "origin", true, "Kyodo, on the scene", "grant", Now.AddMinutes(5));
        store.DeskChecks("e1").Should().ContainSingle().Which.Evidence.Should().Be("Kyodo, on the scene");
        FluentActions.Invoking(() => store.SetDeskCheck("e1", "made-up", true, "x", "grant", Now)).Should().Throw<ArgumentException>();

        store.SetBudgetItem(new BudgetItem("e1", "Quake hits Tokyo", "grant", "verifying", "Call the agency", Now, "grant"));
        store.Budget().Should().ContainSingle().Which.Status.Should().Be("verifying");
        FluentActions.Invoking(() => store.SetBudgetItem(new BudgetItem("e1", "t", "", "done-ish", "", Now, "grant"))).Should().Throw<ArgumentException>();
        store.RemoveBudgetItem("e1");
        store.Budget().Should().BeEmpty();

        store.HasEvent("e1").Should().BeTrue();
        store.HasEvent("nope").Should().BeFalse();

        var old = store.AddTip(null, null, "An old tip nobody followed.", null, Now.AddDays(-100));
        var kept = store.AddTip(null, null, "An old tip the desk is following.", null, Now.AddDays(-100));
        store.SetTipStatus(kept.Id, TipStatuses.Following, "e1", "grant");
        var fresh = store.AddTip("Ana", "ana@example.org", "Smoke over the port.", "https://example.org/p", Now);

        store.Tips().Select(t => t.Id).Should().BeEquivalentTo([kept.Id, fresh.Id]);
        store.Tips().Should().NotContain(t => t.Id == old.Id);
        store.Tips().Single(t => t.Id == kept.Id).Should().Match<NewsTip>(t => t.EventId == "e1" && t.HandledBy == "grant");
    }

    [Fact]
    public void TipEndpoint_ShouldStoreAGoodTipDropABotAndExplainAProblem()
    {
        using var test = new WorldNewsTestStore();

        var sent = WorldNewsTipEndpoints.Accept(new("Ana", null, "Smoke over the port at 14:00.", null, null), test.Store, Now);
        var bot = WorldNewsTipEndpoints.Accept(new("Bot", null, "Buy cheap watches online today.", null, "https://spam.example"), test.Store, Now);
        var bad = WorldNewsTipEndpoints.Accept(new(null, null, "hi", null, null), test.Store, Now);

        sent.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/tip?sent=1");
        bot.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/tip?sent=1");
        bad.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().StartWith("/tip?error=");
        test.Store.Tips().Should().ContainSingle().Which.Name.Should().Be("Ana");
    }

    [Fact]
    public void OfficialNewsrooms_ShouldShowAsStatements_AndNotCountAsIndependentSources()
    {
        WorldArticle Report(string outlet, string id) =>
            new(id, $"https://{id}.example/a", "Ceasefire agreed in the region", outlet, "English", "United Kingdom", Now, "test", null);
        var articles = new[] { Report("BBC News", "a"), Report("The White House", "b"), Report("UN News", "c") };

        SourceIndependence.Assess(articles).Independent.Should().Be(1, "only the BBC is independent journalism here");
        OfficialStatements.In(articles).Select(s => s.Outlet).Should().BeEquivalentTo("The White House", "UN News");
        StoryConfidenceRules.RulesVersion.Should().Be("2026-11-13.2");
        TrustMethodology.Changelog[0].Version.Should().Be(StoryConfidenceRules.RulesVersion, "every rules change is in the changelog");
    }

    [Fact]
    public void SummaryCheck_ShouldFlagSentencesNamingWhatTheReportsDont()
    {
        var sources = new[] { "Earthquake of magnitude 6.1 strikes near Kushiro, Japan", "At least 1,200 people evacuated in Hokkaido" };
        var result = SummaryCheck.Check(
            "A magnitude 6.1 earthquake struck near Kushiro. About 1,200 people were evacuated in Hokkaido. Officials in Sapporo said 40 were hurt. Rescue work continues.",
            sources);

        result.Should().HaveCount(4);
        result[0].Supported.Should().BeTrue();
        result[1].Supported.Should().BeTrue("1,200 matches whatever the separators");
        result[2].NotFound.Should().BeEquivalentTo("Sapporo", "40");
        result[3].Supported.Should().BeTrue("nothing checkable in it");
    }

    [Fact]
    public void BodyChanges_ShouldIgnoreSmallWordingFixes_ButCatchARewrite()
    {
        var original = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"word{i}"));
        BodyChanges.IsMaterial(original, original.Replace("word30", "wordX")).Should().BeFalse();
        BodyChanges.IsMaterial(original, "Officials now say the earlier report was wrong and the strike hit a different town entirely today.").Should().BeTrue();
    }

    [Fact]
    public void Store_ShouldKeepTheFirstTextOfAnEditedArticle()
    {
        using var test = new WorldNewsTestStore();
        test.Store.SaveBodyEdit("a1", "first", "second", Now);
        test.Store.SaveBodyEdit("a1", "second", "third", Now.AddHours(1));
        var edit = test.Store.BodyEdits(["a1", "missing"])["a1"];
        edit.FirstText.Should().Be("first");
        edit.LatestText.Should().Be("third");
    }

    [Fact]
    public void ReleaseStaleVectors_ShouldDropOldPlacedVectorsAndHints_AndKeepWhatClusteringNeeds()
    {
        using var test = new WorldNewsTestStore();
        var now = WorldNewsTestStore.Now;
        test.SeedEvent("s1", "old", "Old story", ["A", "B"], now.AddDays(-10));
        test.SeedEvent("s2", "new", "New story", ["A", "B"], now.AddHours(-2));
        // Age the old one past the window (SeedEvent stamps last_seen an hour ago).
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={test.Path}"))
        {
            connection.Open();
            using var age = connection.CreateCommand();
            age.CommandText = "UPDATE events SET last_seen = $old WHERE id = 'old'; UPDATE articles SET seen_at = $old WHERE event_id = 'old'";
            age.Parameters.AddWithValue("$old", now.AddDays(-10).ToUnixTimeSeconds());
            age.ExecuteNonQuery();
        }

        test.Store.ReleaseStaleVectors(now).Should().BeGreaterThan(0);
        test.Store.EmbeddingDimensions().Should().Be(2, "dimensions come from an event that still has its vector");
        test.Store.ActiveEvents(now.AddDays(-2)).Single(e => e.Id == "new").Sum.Should().HaveCount(2);
        EventClusterer.Dot([], [1f, 0f]).Should().Be(0);
    }
}
