using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News improvements (20-11-26): disagreements claim by claim, a personal Today, and reading
// a report translated.
public sealed class WorldNewsImprovementsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"worldnews-improvements-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    private const string Answer = """
        CLAIM: A ferry sank off the coast, the reports agree.
        SUMMARY: A ferry sank and rescuers searched the water for survivors through the night.
        AGREED:
        - The ferry sank at night [BBC News, Reuters]
        DIFFERENCES:
        - Reuters: names the operator.
        DISPUTES:
        - How many died :: BBC News: at least 12 | Reuters: 9 confirmed | Unknown Times: 40
        - The cause :: BBC News: a storm
        - none
        """;

    [Fact]
    public void ParseDisputes_ShouldKeepPointsTwoOfTheEventsOutletsDisagreeOn()
    {
        var disputes = EventAnalyzer.ParseDisputes(Answer, ["BBC News", "Reuters"]);
        disputes.Should().ContainSingle();
        disputes[0].Point.Should().Be("How many died");
        disputes[0].Accounts.Select(a => (a.Outlet, a.Text)).Should().Equal(("BBC News", "at least 12"), ("Reuters", "9 confirmed"));
        EventAnalyzer.ParseDisputes("CLAIM: nothing else here at all", ["BBC News"]).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldStopDifferencesAtTheDisputesSection()
    {
        var parsed = EventAnalyzer.Parse(Answer, ["BBC News", "Reuters"]);
        parsed!.Value.Differences.Should().ContainSingle().Which.Outlet.Should().Be("Reuters");
    }

    [Fact]
    public void Disputes_ShouldRoundTripThroughTheStore()
    {
        var store = new WorldNewsStore(_path);
        var disputes = EventAnalyzer.ParseDisputes(Answer, ["BBC News", "Reuters"]);
        ClaimDispute.Decode(ClaimDispute.Encode(disputes)).Should().BeEquivalentTo(disputes);

        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var saved = store.SaveAnalysis(new EventAnalysis("e1", "A claim long enough to keep.", "A summary.", [], [], "model", 4, 1, now, disputes));
        saved.Disputes.Should().BeEquivalentTo(disputes);
        // An older analysis (no disputes) still reads.
        store.SaveAnalysis(new EventAnalysis("e2", "Another claim long enough.", "A summary.", [], [], "model", 2, 0, now)).Disputes.Should().BeNull();
    }

    [Fact]
    public void TodayPins_ShouldLeadWithThePinned_AndBeKeptPerPerson()
    {
        TodayPins.Lead([1, 2, 3, 4], n => n % 2 == 0).Should().Equal(2, 4, 1, 3);

        var store = new WorldNewsStore(_path);
        var now = DateTimeOffset.UtcNow;
        store.SetTodayPin("Grant", TodayPins.Country, "Japan", on: true, now);
        store.SetTodayPin("grant", TodayPins.Kind, "disaster", on: true, now);
        store.SetTodayPin("grant", "nonsense", "x", on: true, now);
        store.TodayPinsFor("GRANT").Should().BeEquivalentTo([new TodayPin(TodayPins.Country, "Japan"), new TodayPin(TodayPins.Kind, "disaster")]);
        store.TodayPinsFor("someone-else").Should().BeEmpty();
        store.SetTodayPin("grant", TodayPins.Country, "Japan", on: false, now);
        store.TodayPinsFor("grant").Should().ContainSingle().Which.Kind.Should().Be(TodayPins.Kind);
    }

    [Fact]
    public void TranslationChunks_ShouldSplitOnParagraphs_WithinTheLimit()
    {
        var markdown = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => new string((char)('a' + i), 40)));
        var chunks = ArticleReaderService.Chunks(markdown, 100);
        chunks.Should().HaveCount(3).And.OnlyContain(c => c.Length <= 100);
        string.Join("\n\n", chunks).Should().Be(markdown);
        ArticleReaderService.Chunks(new string('x', 250), 100).Select(c => c.Length).Should().Equal(100, 100, 50);
    }
}
