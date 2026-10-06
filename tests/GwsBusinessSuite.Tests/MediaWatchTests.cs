using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Tests;

public sealed class MediaWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private static NewsItemDto Item(string title, string source, double hoursAgo, string? summary = null, string? url = null) =>
        new(Guid.NewGuid(), null, "Top News", "#64748b", title, url ?? $"https://{source.Replace(" ", "").ToLowerInvariant()}.example/{Guid.NewGuid():N}",
            source, Now.AddHours(-hoursAgo), "desc", summary ?? string.Empty, Now.AddHours(-hoursAgo));

    // ---- Story grouping and "breaking" -----------------------------------------------------

    [Fact]
    public void Cluster_ShouldGroupTheSameStoryAcrossOutlets_IgnoringTheGoogleNewsOutletSuffix()
    {
        var items = new[]
        {
            Item("Federal Reserve holds interest rates steady amid inflation worries - Reuters", "Reuters", 2),
            Item("Fed holds interest rates steady as inflation worries linger - AP News", "AP News", 1.5, summary: "The Fed blinked."),
            Item("Federal Reserve keeps interest rates steady, citing inflation - CNBC", "CNBC", 1),
            Item("NASA launches new Mars rover mission from Cape Canaveral - Space.com", "Space.com", 3),
        };

        var stories = NewsStoryClusterer.Cluster(items, Now);

        stories.Should().HaveCount(2);
        var fed = stories.Single(s => s.Lead.Title.Contains("Fed"));
        fed.OutletCount.Should().Be(3);
        fed.Lead.Source.Should().Be("AP News", "the report with an AI take leads");
        fed.IsBreaking.Should().BeTrue("three outlets within six hours");
        stories[0].Should().Be(fed, "breaking stories sort first");
        stories.Single(s => s.Lead.Source == "Space.com").IsBreaking.Should().BeFalse();
    }

    [Fact]
    public void Breaking_ShouldNeedFreshCoverage_NotJustASignalWord()
    {
        NewsStoryClusterer.Cluster([Item("Breaking: bridge closed after crash on I-75", "WMAZ", 1)], Now)
            .Single().IsBreaking.Should().BeTrue("a signal word on a fresh headline");
        NewsStoryClusterer.Cluster([Item("Breaking: bridge closed after crash on I-75", "WMAZ", 10)], Now)
            .Single().IsBreaking.Should().BeFalse("ten hours old is not breaking");
        NewsStoryClusterer.Cluster([Item("Urgent care clinic opens in Perry", "Local", 1)], Now)
            .Single().IsBreaking.Should().BeFalse("\"urgent\" alone is no longer a breaking signal");
    }

    [Fact]
    public void UrlHash_ShouldIgnoreSchemeWwwAndTrailingSlash()
    {
        NewsStoryClusterer.UrlHash("https://www.example.com/story/1/")
            .Should().Be(NewsStoryClusterer.UrlHash("http://example.com/story/1"))
            .And.NotBe(NewsStoryClusterer.UrlHash("https://example.com/story/2"));
    }

    // ---- Trends ----------------------------------------------------------------------------

    [Fact]
    public void RisingTerms_ShouldFlagWordsUsedMoreRecently()
    {
        var today = new DateOnly(2026, 10, 6);
        var days = new List<(DateOnly, Dictionary<string, int>)>();
        for (var i = 2; i < 12; i++) days.Add((today.AddDays(-i), new() { ["election"] = 2, ["weather"] = 1 }));
        days.Add((today.AddDays(-1), new() { ["election"] = 2, ["hurricane"] = 6 }));
        days.Add((today, new() { ["election"] = 2, ["hurricane"] = 9, ["weather"] = 5 }));

        var rising = NewsTrendTerms.Rising(days, today);

        rising.Select(r => r.Term).Should().Equal("hurricane", "weather");
        rising[0].Ratio.Should().BeNull("never seen before the last two days");
        rising[1].Ratio.Should().BeApproximately(2.5, 0.01);
        rising.Should().NotContain(r => r.Term == "election", "steady coverage isn't rising");
    }

    [Fact]
    public void TrendTermMerge_ShouldKeepOnlyTheTopTerms()
    {
        var merged = NewsTrendTerms.Merge(new() { ["a1"] = 5 }, Enumerable.Range(0, 100).ToDictionary(i => $"t{i}", i => i + 1), keep: 10);
        merged.Should().HaveCount(10).And.ContainKey("t99").And.NotContainKey("a1");
    }

    // ---- Feed discovery --------------------------------------------------------------------

    [Fact]
    public void AlternateLinks_ShouldResolveRelativeRssAndAtomFeeds()
    {
        const string html = """
            <html><head>
              <link rel="stylesheet" href="/site.css">
              <link rel="alternate" type="application/rss+xml" title="Posts" href="/feed.xml">
              <link type="application/atom+xml" href="https://cdn.example.org/atom" rel="alternate">
              <link rel="alternate" hreflang="fr" href="/fr/">
            </head></html>
            """;

        NewsWatchService.FindAlternateFeedLinks(html, new Uri("https://example.org/blog/"))
            .Should().Equal("https://example.org/feed.xml", "https://cdn.example.org/atom");
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("93.184.216.34", true)]
    public void FeedDiscovery_ShouldOnlyReachPublicAddresses(string ip, bool expected)
    {
        NewsWatchService.IsPublicAddress(IPAddress.Parse(ip)).Should().Be(expected);
    }

    [Fact]
    public async Task FeedDiscovery_ShouldFollowTheAlternateLink()
    {
        const string rss = """<?xml version="1.0"?><rss version="2.0"><channel><title>Example Posts</title><item><title>One</title><link>https://example.org/1</link></item></channel></rss>""";
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Html("""<link rel="alternate" type="application/rss+xml" href="/posts.rss">"""),
            "/posts.rss" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(rss, Encoding.UTF8, "application/rss+xml") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var feeds = await fixture.Service.DiscoverFeedsAsync("93.184.216.34");

        feeds.Should().ContainSingle().Which.Should().Be(new DiscoveredFeed("https://93.184.216.34/posts.rss", "Example Posts", 1));
        await fixture.Invoking(f => f.Service.DiscoverFeedsAsync("http://localhost:8080")).Should().ThrowAsync<ArgumentException>();
    }

    // ---- Read state, saved, clips ----------------------------------------------------------

    [Fact]
    public async Task ReadMarks_ShouldBePerUser_AndDriveUnreadCounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var topic = new WatchedTopic { Name = "Fed" };
        fixture.Db.WatchedTopics.Add(topic);
        fixture.Db.NewsItems.AddRange(
            NewsRow(null, "https://example.com/a"), NewsRow(null, "https://example.com/b"), NewsRow(topic.Id, "https://example.com/c"));
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.MarkReadAsync("grant", ["https://www.example.com/a/"]);

        var grant = await fixture.Service.GetUnreadCountsAsync("grant");
        grant[Guid.Empty].Should().Be(1);
        grant[topic.Id].Should().Be(1);
        (await fixture.Service.GetUnreadCountsAsync("someone-else"))[Guid.Empty].Should().Be(2);
    }

    [Fact]
    public async Task Saving_ShouldCopyTheArticle_AndClippingShouldNestUnderOneParentPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = Item("Fed holds rates [steady](javascript:alert(1))", "Reuters", 1, summary: "Calm.");

        await fixture.Service.SaveArticleAsync("grant", item);
        await fixture.Service.SaveArticleAsync("grant", item);
        (await fixture.Service.ListSavedAsync("grant")).Should().ContainSingle(s => s.Summary == "Calm.");

        var pageId = await fixture.Service.ClipToSentinelAsync("grant", item);
        var second = await fixture.Service.ClipToSentinelAsync("grant", Item("Another story about rates", "AP", 1));

        fixture.Wiki.Saved.Select(p => p.Title).Should().Equal(NewsWatchService.ClipsParentTitle, item.Title, "Another story about rates");
        fixture.Wiki.Saved[1].ParentWikiPageId.Should().Be(fixture.Wiki.Ids[0]);
        fixture.Wiki.Saved[1].BlocksJson.Should().NotContain("](javascript", "feed text can't smuggle in links");
        (await fixture.Service.ListSavedAsync("grant")).Single(s => s.Url == item.Url).ClippedWikiPageId.Should().Be(pageId);
        second.Should().NotBe(pageId);

        await fixture.Service.UnsaveArticleAsync("grant", item.Url);
        (await fixture.Service.GetSavedUrlHashesAsync("grant")).Should().HaveCount(1);
    }

    // ---- Settings and digest ---------------------------------------------------------------

    [Theory]
    [InlineData("daily", 7, 1, 8, null, true)]
    [InlineData("daily", 7, 1, 6, null, false)]
    [InlineData("daily", 7, 1, 9, 0, false)]
    [InlineData("weekly", 7, 2, 9, null, true)]   // 2026-10-06 is a Tuesday
    [InlineData("weekly", 7, 1, 9, null, false)]
    public void DigestDue_ShouldRespectFrequencyDayAndHour(string frequency, int hour, int dayOfWeek, int nowHour, int? sentDaysAgo, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 6, nowHour, 0, 0, TimeSpan.FromHours(-4));
        var settings = new NewsWatchSettings
        {
            DigestEnabled = true, DigestFrequency = frequency, DigestHourLocal = hour, DigestDayOfWeek = dayOfWeek,
            LastDigestSentAt = sentDaysAgo is { } d ? new DateTimeOffset(2026, 10, 6, 7, 5, 0, now.Offset).AddDays(-d) : null
        };
        NewsWatchService.DigestDue(settings, now).Should().Be(expected);
    }

    [Fact]
    public async Task Settings_ShouldRejectUnknownRetentionAndBadEmail()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Invoking(f => f.Service.SaveSettingsAsync(new NewsWatchSettingsView(5, false, null, "daily", 7, 1)))
            .Should().ThrowAsync<ArgumentException>();
        await fixture.Invoking(f => f.Service.SaveSettingsAsync(new NewsWatchSettingsView(48, true, "not-an-email", "daily", 7, 1)))
            .Should().ThrowAsync<ArgumentException>();

        await fixture.Service.SaveSettingsAsync(new NewsWatchSettingsView(72, true, "me@example.com", "weekly", 8, 5));
        var saved = await fixture.Service.GetSettingsAsync();
        saved.RetentionHours.Should().Be(72);
        saved.DigestFrequency.Should().Be("weekly");
        saved.CanSendEmail.Should().BeTrue();
    }

    [Fact]
    public async Task Digest_ShouldGroupStoriesPerTopic_AndSkipWhenNothingIsNew()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveSettingsAsync(new NewsWatchSettingsView(24, true, "me@example.com", "daily", 0, 1));
        var topic = new WatchedTopic { Name = "Rates" };
        fixture.Db.WatchedTopics.Add(topic);
        fixture.Db.NewsItems.AddRange(
            NewsRow(topic.Id, "https://reuters.example/1", "Fed holds interest rates steady amid inflation worries", "Reuters"),
            NewsRow(topic.Id, "https://ap.example/1", "Fed holds interest rates steady as inflation worries linger", "AP"));
        await fixture.Db.SaveChangesAsync();

        var sent = await fixture.Service.SendDigestAsync(force: true);

        sent.Sent.Should().BeTrue();
        var message = fixture.Mail.Sent.Should().ContainSingle().Subject;
        message.Subject.Should().Contain("1 stories");
        message.TextBody.Should().Contain("RATES").And.Contain("+ 1 more");

        await fixture.Db.NewsItems.ExecuteDeleteAsync();
        (await fixture.Service.SendDigestAsync(force: true)).Sent.Should().BeFalse();
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static NewsItem NewsRow(Guid? topicId, string url, string title = "A headline worth reading today", string source = "Example")
    {
        var now = DateTimeOffset.UtcNow;
        return new NewsItem
        {
            TopicId = topicId, Title = title, Url = url, Source = source,
            PublishedAt = now, PublishedAtUnixSeconds = now.ToUnixTimeSeconds(), FetchedAt = now, FetchedAtUnixSeconds = now.ToUnixTimeSeconds()
        };
    }

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"<html><head>{body}</head></html>", Encoding.UTF8, "text/html") };

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        public ApplicationDbContext Db { get; private set; } = null!;
        public NewsWatchService Service { get; private set; } = null!;
        public RecordingWiki Wiki { get; } = new();
        public RecordingMail Mail { get; } = new();

        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage>? http = null)
        {
            var fixture = new Fixture { _connection = new SqliteConnection("Data Source=:memory:") };
            await fixture._connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(fixture._connection).Options;
            fixture.Db = new ApplicationDbContext(options);
            await fixture.Db.Database.EnsureCreatedAsync();
            fixture.Wiki.Persist = page =>
            {
                using var db = new ApplicationDbContext(options);
                db.WikiPages.Add(page);
                db.SaveChanges();
            };
            fixture.Service = new NewsWatchService(
                new Factory(options),
                fixture.Wiki.Service,
                new HttpClient(new Handler(http ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
                fixture.Mail,
                Options.Create(new GrowthReportEmailOptions { Host = "smtp.example.com", FromAddress = "noreply@example.com" }),
                TimeProvider.System,
                NullLogger<NewsWatchService>.Instance);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class RecordingMail : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public MailRoute Describe(ISmtpTransportOptions options) => new(MailRouteKind.Smtp, "test");
        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Only SavePageAsync is used; the rest of IWikiService is left unimplemented via DispatchProxy.
    private sealed class RecordingWiki
    {
        public List<WikiPageEditorModel> Saved { get; } = [];
        public List<Guid> Ids { get; } = [];
        public Action<WikiPage>? Persist { get; set; }
        public IWikiService Service { get; }

        public RecordingWiki()
        {
            Service = System.Reflection.DispatchProxy.Create<IWikiService, WikiProxy>();
            ((WikiProxy)(object)Service).Owner = this;
        }
    }

    public class WikiProxy : System.Reflection.DispatchProxy
    {
        internal object? Owner { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IWikiService.SavePageAsync))
                throw new NotSupportedException($"{targetMethod?.Name} isn't used by these tests.");
            var editor = (WikiPageEditorModel)args![0]!;
            var owner = (RecordingWiki)Owner!;
            var page = new WikiPage { Title = editor.Title, Slug = $"page-{owner.Saved.Count}", BlocksJson = editor.BlocksJson, ParentWikiPageId = editor.ParentWikiPageId };
            owner.Saved.Add(editor);
            owner.Ids.Add(page.Id);
            owner.Persist?.Invoke(page);
            return Task.FromResult(page);
        }
    }
}
