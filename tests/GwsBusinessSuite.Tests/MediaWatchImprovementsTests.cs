using FluentAssertions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Media Watch improvements (20-11-26): one link per article, grouping by meaning, the reader's
// fallback to kept text, and the digest's ranking and quick reads.
public sealed class MediaWatchImprovementsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Canonical_ShouldReadTheLinkTag_OrOgUrl_AndResolveRelativeAddresses()
    {
        ArticleLinks.Canonical("https://www.example.com/a/b?utm_source=x", """<head><link href="/news/story-1" rel="canonical"></head>""")
            .Should().Be("https://www.example.com/news/story-1");
        ArticleLinks.Canonical("https://example.com/amp/story", """<meta property="og:url" content="https://example.com/story">""")
            .Should().Be("https://example.com/story");
    }

    [Theory]
    [InlineData("""<link rel="canonical" href="https://news.google.com/rss/articles/abc">""")]
    [InlineData("""<link rel="canonical" href="https://example.com/">""")]
    [InlineData("""<link rel="canonical" href="javascript:alert(1)">""")]
    [InlineData("<p>no canonical</p>")]
    public void Canonical_ShouldIgnoreAggregators_HomePages_AndOtherSchemes(string html) =>
        ArticleLinks.Canonical("https://example.com/2026/11/story", html).Should().BeNull();

    [Fact]
    public void Preferred_ShouldTakeTheSameSitesCanonical_AndDropTracking()
    {
        ArticleLinks.Preferred("http://amp.cnn.com/story?utm_source=rss&id=4", "https://www.cnn.com/story?id=4")
            .Should().Be("https://www.cnn.com/story?id=4");
        ArticleLinks.Preferred("https://example.com/a?utm_medium=feed&fbclid=1", null).Should().Be("https://example.com/a");
    }

    [Fact]
    public void Preferred_ShouldKeepASyndicatedCopysOwnLink_ButFollowARedirector()
    {
        ArticleLinks.Preferred("https://news.yahoo.com/story-1", "https://www.reuters.com/world/story-1").Should().Be("https://news.yahoo.com/story-1");
        ArticleLinks.Preferred("https://feeds.feedburner.com/x/abc", "https://blog.example.com/post").Should().Be("https://blog.example.com/post");
    }

    [Fact]
    public void Cluster_ShouldJoinDifferentlyWordedReports_WhenTheirMeaningsAgree()
    {
        var a = Item("Wildfire forces thousands to flee near Los Angeles");
        var b = Item("Blaze sends residents fleeing LA suburbs");
        var c = Item("Central bank holds interest rates steady");
        NewsStoryClusterer.Cluster([a, b, c], Now).Should().HaveCount(3, "no shared words");

        var meanings = new Dictionary<Guid, float[]>
        {
            [a.Id] = [1f, 0.1f, 0f],
            [b.Id] = [0.95f, 0.15f, 0f],
            [c.Id] = [0f, 0f, 1f],
        };
        var stories = NewsStoryClusterer.Cluster([a, b, c], Now, meanings);
        stories.Should().HaveCount(2);
        stories.Should().ContainSingle(s => s.OtherReports.Count == 1);
    }

    [Fact]
    public void SameByMeaning_ShouldNeedAWordUnlessVeryClose()
    {
        NewsStoryClusterer.SameByMeaning(0.75, shareAWord: true).Should().BeTrue();
        NewsStoryClusterer.SameByMeaning(0.75, shareAWord: false).Should().BeFalse();
        NewsStoryClusterer.SameByMeaning(0.9, shareAWord: false).Should().BeTrue();
        NewsStoryClusterer.Cosine([1f, 0f], [0f, 1f]).Should().Be(0);
        NewsStoryClusterer.Cosine([1f, 2f], [1f]).Should().Be(0, "different lengths never match");
    }

    [Fact]
    public void FromKept_ShouldShowPlainTextAsParagraphs_WithoutAnyMarkup()
    {
        var kept = new KeptPage("https://example.com/s", "A title", "First line <script>alert(1)</script>\n\nSecond *line*", Now);
        var article = ArticleReaderService.FromKept(new Uri("https://example.com/s"), kept, "fallback");
        article.Readable.Should().BeTrue();
        article.Title.Should().Be("A title");
        article.Html.Should().NotContain("<script>").And.Contain("crawler kept").And.Contain("Second *line*");
    }

    [Fact]
    public void QuickReads_ShouldBeUnfinishedShortReads_ShortestFirst()
    {
        var saved = new[]
        {
            Saved("long", 25), Saved("eight", 8), Saved("three", 3), Saved("done", 2, finished: true), Saved("unknown", null), Saved("ten", 10),
        };
        NewsWatchService.QuickReads(saved).Select(s => s.Title).Should().Equal("three", "eight", "ten");
    }

    [Fact]
    public void DigestStories_ShouldLeadTechnicalTopicsWithTheBestReads()
    {
        var shallow = News("Tiny release note", score: 2, minutes: 1);
        var deep = News("How we rebuilt our storage engine", score: 600, minutes: 18);
        NewsWatchService.Stories([shallow, deep], "Software", Now, technical: true)[0].Lead.Title.Should().Be(deep.Title);
        var text = NewsWatchService.DigestText([("Software", NewsWatchService.Stories([deep], "Software", Now, true))], [Saved("Quick one", 4)]);
        text.Should().StartWith("1 QUEUED READS UNDER 10 MINUTES").And.Contain("Quick one (4 min");
    }

    private static NewsItemDto Item(string title) =>
        new(Guid.NewGuid(), null, "Top News", "", title, "https://example.com/" + Guid.NewGuid(), "Outlet", Now.AddHours(-1), "", "", Now);

    private static SavedArticleView Saved(string title, int? minutes, bool finished = false) =>
        new(Guid.NewGuid(), "https://example.com/" + title, title, "Blog", "Software", Now, "", Now, null, minutes, finished ? Now : null);

    private static NewsItem News(string title, int score, int minutes) => new()
    {
        Title = title, Url = "https://example.com/" + Guid.NewGuid(), Source = "Blog", Description = "", OllamaSummary = "",
        PublishedAt = Now.AddHours(-2), FetchedAt = Now.AddHours(-1), CommunityScore = score, ReadingMinutes = minutes,
    };
}
