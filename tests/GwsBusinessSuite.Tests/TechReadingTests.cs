using FluentAssertions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Media Watch as a reading desk for software development (20-11-26).
public sealed class TechReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pack_ShouldAddItsFeedsOnce_AndKeepTheTopicsOwn()
    {
        var merged = TechReadingPack.MergeFeeds("https://my.blog/feed\nhttps://feed.infoq.com/");
        var lines = merged.Split('\n');
        lines.Should().StartWith("https://my.blog/feed").And.OnlyHaveUniqueItems();
        lines.Should().HaveCount(TechReadingPack.Feeds.Count + 1);
        TechReadingPack.MergeFeeds(merged).Should().Be(merged);
    }

    [Fact]
    public void BestReads_ShouldFavourDiscussedAndDeepArticles_OverFreshShortOnes()
    {
        var deep = TechReadingRank.Score(400, 15, Now.AddDays(-1), Now);
        var shallow = TechReadingRank.Score(null, 1, Now.AddHours(-1), Now);
        var stale = TechReadingRank.Score(400, 15, Now.AddDays(-20), Now);
        deep.Should().BeGreaterThan(shallow);
        deep.Should().BeGreaterThan(stale);
        TechReadingRank.MinutesFor(string.Join(' ', Enumerable.Repeat("word", 2300))).Should().Be(10);
        TechReadingRank.MinutesFor("").Should().Be(0);
    }

    [Theory]
    [InlineData("dotnet", "What's new in Blazor for .NET 10", true)]
    [InlineData("ai", "Running a local LLM with Ollama", true)]
    [InlineData("ai", "Maintaining a big codebase", false)]
    [InlineData("security", "A new CVE in OpenSSH", true)]
    [InlineData("apple", "SwiftUI layout tips", true)]
    public void StackTags_ShouldMatchTheirTerms(string tag, string title, bool expected) =>
        ReadingFilters.Matches(tag, title, null).Should().Be(expected);

    [Fact]
    public void Mutes_ShouldHideByKeywordOutletOrSite()
    {
        var keywords = ReadingFilters.Lines("crypto\n top 10 ");
        var sources = ReadingFilters.Lines("medium.com\nSpam Weekly");
        ReadingFilters.IsMuted("Crypto winter", null, "X", "https://x.dev/a", keywords, sources).Should().BeTrue();
        ReadingFilters.IsMuted("A post", null, "Y", "https://blog.medium.com/p", keywords, sources).Should().BeTrue();
        ReadingFilters.IsMuted("A post", null, "Spam Weekly", "https://spam.dev/p", keywords, sources).Should().BeTrue();
        ReadingFilters.IsMuted("Top 10 tips", null, "Z", "https://z.dev", keywords, sources).Should().BeTrue();
        ReadingFilters.IsMuted("Event sourcing in practice", null, "Z", "https://z.dev", keywords, sources).Should().BeFalse();
    }

    [Fact]
    public void Queue_ShouldShowWhatFits_SortByLength_AndHideFinished()
    {
        SavedArticleView Saved(string title, int? minutes, bool finished = false, int daysAgo = 1) =>
            new(Guid.NewGuid(), "https://x.dev/" + title, title, "S", "T", null, "", Now.AddDays(-daysAgo), null, minutes, finished ? Now : null);
        var queue = new[] { Saved("long", 25), Saved("short", 4), Saved("mid", 12), Saved("unknown", null), Saved("done", 3, finished: true) };

        ReadingQueue.Arrange(queue, 15, ReadingQueue.SortShortest, false).Select(s => s.Title).Should().Equal("short", "mid");
        ReadingQueue.Arrange(queue, null, ReadingQueue.SortLongest, false).Select(s => s.Title).Should().Equal("long", "mid", "short", "unknown");
        ReadingQueue.Arrange(queue, null, ReadingQueue.SortSaved, true).Should().HaveCount(5);
    }

    [Fact]
    public void Takeaways_ShouldParseTheModelsAnswer()
    {
        var parsed = ArticleReaderService.ParseTakeaways("- Use retries with jitter.\n- Keep idempotency keys.\n2. Measure tail latency.\n**For GWS:** The mail sync could use jitter.");
        parsed.Points.Should().Equal("Use retries with jitter.", "Keep idempotency keys.", "Measure tail latency.");
        parsed.ForGws.Should().Be("The mail sync could use jitter.");
        ArticleReaderService.ParseTakeaways("- One.\n- Two.\n- Three.\n- **For GWS**: Not relevant.").ForGws.Should().Be("Not relevant.");
        ArticleReaderService.ParseTakeaways("no list here").Problem.Should().NotBeNull();
    }

    [Fact]
    public void WeeklyPage_ShouldLinkEachPick_WithItsFacts()
    {
        var md = ReadingDigest.Markdown([new("EF Core [deep] dive", "https://a.example/ef", "InfoQ", "SD", Now, 300, 14, "A take.")], Now);
        md.Should().Contain("1. [EF Core \\[deep\\] dive](https://a.example/ef) · InfoQ · 14 min read · 300 points");
        md.Should().Contain("A take.");
    }
}
