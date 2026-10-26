using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.WebCrawl;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class WebCrawlTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"webcrawl-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
    }

    [Fact]
    public void Queries_ShouldQuoteEveryWordAndPhrase_SoNothingReachesFts5Syntax()
    {
        WebSearchQuery.FromSearch("atlanta \"light rail\" NEAR(x) -").Should().Be("\"atlanta\" \"light rail\" \"NEAR(x)\"");
        WebSearchQuery.FromSearch("say \"\"hi").Should().Be("\"say\" \"hi\"");
        WebSearchQuery.FromSearch("  - * ").Should().BeNull();
        WebSearchQuery.FromKeywords("MARTA, light rail ,, \"quoted\"").Should().Be("\"MARTA\" OR \"light rail\" OR \"\"\"quoted\"\"\"");
        WebSearchQuery.FromKeywords(" , ").Should().BeNull();
    }

    [Fact]
    public void Robots_ShouldUseOurGroupOrStar_LongestMatchWinning_WithWildcards()
    {
        const string text = """
            # comment
            User-agent: *
            Disallow: /private/
            Allow: /private/press
            Disallow: /*.pdf$
            Crawl-delay: 5

            User-agent: GwsBusinessSuite-MediaWatch
            User-agent: other
            Disallow: /drafts
            Sitemap: https://news.test/sitemap.xml
            """;
        var star = RobotsTxt.Parse(text, "SomeoneElse");
        star.IsAllowed("/private/x").Should().BeFalse();
        star.IsAllowed("/private/press/release").Should().BeTrue("the longer Allow wins");
        star.IsAllowed("/files/report.pdf").Should().BeFalse();
        star.IsAllowed("/files/report.pdf?x=1").Should().BeTrue("$ anchors the end");
        star.CrawlDelaySeconds.Should().Be(5);
        star.Sitemaps.Should().Equal("https://news.test/sitemap.xml");

        var ours = RobotsTxt.Parse(text, WebCrawler.ProductToken);
        ours.IsAllowed("/private/x").Should().BeTrue("our own group replaces the * group");
        ours.IsAllowed("/drafts/1").Should().BeFalse();
        ours.IsAllowed("/robots.txt").Should().BeTrue();

        RobotsTxt.Parse("User-agent: *\nDisallow:", "x").IsAllowed("/anything").Should().BeTrue("an empty Disallow allows all");
        RobotsTxt.Parse("User-agent: *\nDisallow: /", "x").IsAllowed("/a").Should().BeFalse();
        RobotsTxt.Parse("", "x").IsAllowed("/a").Should().BeTrue();
    }

    [Fact]
    public void Normalize_ShouldGiveOnePageOneAddress()
    {
        var page = new Uri("https://News.test/section/");
        WebPageReader.Normalize("../2026/story?utm_source=x&id=7&fbclid=y#top", page)!.AbsoluteUri.Should().Be("https://news.test/2026/story?id=7");
        WebPageReader.Normalize("HTTPS://NEWS.TEST:443/a", page)!.AbsoluteUri.Should().Be("https://news.test/a");
        WebPageReader.Normalize("mailto:x@y.z", page).Should().BeNull();
        WebPageReader.Normalize("javascript:void(0)", page).Should().BeNull();
        WebPageReader.Normalize("/photo.JPG", page).Should().BeNull();
        WebPageReader.Normalize("#comments", page).Should().BeNull();
    }

    private const string ArticleHtml = """
        <html><head><title>Ignored title</title>
          <meta property="og:title" content="Atlanta approves MARTA expansion">
          <meta name="description" content="The council voted.">
          <meta property="article:published_time" content="2026-10-26T09:00:00Z">
          <link rel="canonical" href="/2026/10/26/story">
        </head><body>
          <nav><a href="/">Home</a></nav>
          <article><h1>Atlanta approves MARTA expansion</h1>
            <p>The city council voted on Monday to fund a MARTA expansion along the Beltline, adding light rail to the east side.</p>
            <p>Supporters said the line would carry thousands of riders a day. Opponents worried about the cost and the years of construction ahead.</p>
            <p>The first trains could run by 2031, according to the transit agency, which still needs federal money for the rest of the project.</p>
          </article>
          <a href="/2026/10/26/second">Next story</a>
          <a href="/private/secret">Members</a>
          <a href="https://other.test/page" rel="nofollow">Sponsored</a>
          <a href="https://third.test/">Partner</a>
        </body></html>
        """;

    [Fact]
    public void ReadHtml_ShouldFindTitleDateTextAndLinks_AndHonourRobotsMeta()
    {
        var page = WebPageReader.ReadHtml(ArticleHtml, new Uri("https://news.test/2026/10/26/story?ref=home"));

        page.Title.Should().Be("Atlanta approves MARTA expansion");
        page.Description.Should().Be("The council voted.");
        page.PublishedAt.Should().Be(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        page.Canonical!.AbsoluteUri.Should().Be("https://news.test/2026/10/26/story");
        page.Text.Should().Contain("MARTA expansion along the Beltline");
        page.Links.Select(l => l.AbsoluteUri).Should().Contain("https://news.test/2026/10/26/second").And.Contain("https://third.test/")
            .And.NotContain("https://other.test/page", "rel=nofollow links aren't followed");
        page.NoIndex.Should().BeFalse();

        var hidden = WebPageReader.ReadHtml("<html><head><meta name='robots' content='noindex, nofollow'></head><body><a href='/x'>x</a></body></html>",
            new Uri("https://news.test/"));
        hidden.NoIndex.Should().BeTrue();
        hidden.Links.Should().BeEmpty();

        var ldJson = WebPageReader.ReadHtml("""<html><body><script type="application/ld+json">{"datePublished": "2026-10-20T08:00:00-04:00"}</script></body></html>""",
            new Uri("https://news.test/"));
        ldJson.PublishedAt.Should().Be(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ReadXml_ShouldReadSitemapsAndFeeds_NewestFirst_AndRefuseDtds()
    {
        var url = new Uri("https://news.test/sitemap.xml");
        var index = WebPageReader.ReadXml("""
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://news.test/old.xml</loc><lastmod>2026-01-01</lastmod></sitemap>
              <sitemap><loc>https://news.test/new.xml</loc><lastmod>2026-10-25</lastmod></sitemap>
            </sitemapindex>
            """, url);
        index.Sitemaps.Select(s => s.Url.AbsoluteUri).Should().Equal("https://news.test/new.xml", "https://news.test/old.xml");

        var news = WebPageReader.ReadXml("""
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:news="http://www.google.com/schemas/sitemap-news/0.9">
              <url><loc>https://news.test/a</loc><news:news><news:publication_date>2026-10-26T08:00:00Z</news:publication_date></news:news></url>
              <url><loc>https://news.test/b</loc><lastmod>2026-10-24</lastmod></url>
            </urlset>
            """, url);
        news.Pages.Select(p => p.Url.AbsoluteUri).Should().Equal("https://news.test/a", "https://news.test/b");

        var rss = WebPageReader.ReadXml("<rss><channel><item><link>https://cdn.news.test/story</link><pubDate>Mon, 26 Oct 2026 10:00:00 GMT</pubDate></item></channel></rss>", url);
        rss.Pages.Should().ContainSingle().Which.Date.Should().Be(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));

        var atom = WebPageReader.ReadXml("""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><link rel="alternate" href="https://news.test/atom-story"/><updated>2026-10-26T11:00:00Z</updated></entry></feed>
            """, url);
        atom.Pages.Should().ContainSingle().Which.Url.AbsoluteUri.Should().Be("https://news.test/atom-story");

        WebPageReader.ReadXml("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><urlset><url><loc>&e;</loc></url></urlset>""", url)
            .Pages.Should().BeEmpty("DTDs (and so external entities) are refused");
        WebPageReader.ReadXml("not xml", url).Pages.Should().BeEmpty();
    }

    [Fact]
    public async Task Store_ShouldGiveEachSiteOneTurnAtATime_IndexPages_AndPrune()
    {
        var store = new WebCrawlStore(_dbPath);
        await store.EnsureSiteAsync("a.test", "https://a.test/", 0, 1000);
        await store.EnsureSiteAsync("b.test", "https://b.test/", 0, 1000);
        (await store.EnqueueAsync([
            ("https://a.test/", "a.test", true, 3600, 0, 9), ("https://a.test/1", "a.test", false, 0, 1, 5),
            ("https://b.test/1", "b.test", false, 0, 1, 5), ("https://a.test/1", "a.test", false, 0, 1, 5)], 1000)).Should().Be(3);

        var first = await store.TakeNextAsync(2000, 10);
        first.Select(e => e.Url).Should().Equal("https://a.test/", "https://b.test/1");
        (await store.TakeNextAsync(2000, 10)).Should().BeEmpty("both sites are taken until their fetches are recorded");

        await store.MarkFetchedAsync("https://a.test/", "a.test", 200, 2000, 3000, null);
        (await store.TakeNextAsync(2500, 10)).Should().BeEmpty("a.test's wait runs until 3000");
        (await store.TakeNextAsync(3000, 10)).Select(e => e.Url).Should().Equal("https://a.test/1");
        await store.MarkFetchedAsync("https://a.test/1", "a.test", 200, 3000, 4000, null);
        (await store.TakeNextAsync(5000, 10)).Should().BeEmpty("the home page hub isn't due again for an hour");
        (await store.TakeNextAsync(2000 + 3_600_000, 10)).Select(e => e.Url).Should()
            .Equal(["https://a.test/", "https://b.test/1"], "the hub is due again, and b.test's hold (never recorded) has run out");

        (await store.AddPageAsync("https://a.test/1", "a.test", "MARTA expansion approved", null, 100, "Light rail along the Beltline")).Should().BeTrue();
        (await store.AddPageAsync("https://a.test/1", "a.test", "Again", null, 100, "Again")).Should().BeFalse();
        (await store.AddPageAsync("https://b.test/1", "b.test", "Budget", null, 200, "Mentions MARTA once in passing")).Should().BeTrue();
        var hits = await store.SearchAsync("\"MARTA\"", 10);
        hits.Select(h => h.Url).Should().Equal("https://a.test/1", "https://b.test/1"); // title matches weigh more
        hits[0].Snippet.Should().NotBeNull();
        (await store.SearchAsync("\"marta\"", 10, afterId: hits.Min(h => h.Id), oldestFirst: true)).Should().ContainSingle();
        (await store.SearchAsync("\"beltlines\"", 10)).Should().ContainSingle("the porter stemmer matches word forms");

        (await store.PruneAsync(150, 0)).Should().Be(1);
        (await store.SearchAsync("\"MARTA\"", 10)).Select(h => h.Host).Should().Equal("b.test");
        await store.RemoveSiteAsync("b.test");
        (await store.SearchAsync("\"MARTA\"", 10)).Should().BeEmpty();
        (await store.ListSitesAsync(10)).Select(s => s.Host).Should().Equal("a.test");
    }

    [Fact]
    public async Task Crawler_ShouldKeepToRobotsAndWaits_FindSitesThroughLinks_AndFeedTopicMatchesToMediaWatch()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var site = new FakeWeb(clock);
        site.Pages["https://news.test/robots.txt"] = (HttpStatusCode.OK, "text/plain",
            "User-agent: *\nDisallow: /private/\nSitemap: https://news.test/sitemap.xml");
        site.Pages["https://news.test/"] = (HttpStatusCode.OK, "text/html",
            "<html><body><a href='/2026/10/26/story'>Story</a><a href='/private/secret'>Members</a><a href='https://third.test/'>Partner</a></body></html>");
        site.Pages["https://news.test/sitemap.xml"] = (HttpStatusCode.OK, "application/xml", """
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://news.test/2026/10/26/second</loc></url></urlset>
            """);
        site.Pages["https://news.test/2026/10/26/story"] = (HttpStatusCode.OK, "text/html; charset=utf-8", ArticleHtml);
        site.Pages["https://news.test/2026/10/26/second"] = (HttpStatusCode.OK, "text/html",
            "<html><head><title>Weather</title></head><body><p>" + string.Concat(Enumerable.Repeat("Rain is likely across north Georgia this week. ", 12)) + "</p></body></html>");
        site.Pages["https://third.test/robots.txt"] = (HttpStatusCode.NotFound, "text/plain", "");
        site.Pages["https://third.test/"] = (HttpStatusCode.OK, "text/html", "<html><body>Partner home</body></html>");

        await using var app = await TestApp.CreateAsync();
        var crawler = new WebCrawler(site, app.ScopeFactory, Options.Create(new WebCrawlOptions
        {
            DatabasePath = _dbPath, MinDelaySeconds = 1, DailyPageLimit = 1000, ParallelSites = 4, MaxNewSitesPerDay = 5
        }), clock, NullLogger<WebCrawler>.Instance);

        await crawler.AddSiteAsync("news.test");
        for (var round = 0; round < 12; round++)
        {
            await crawler.CrawlOnceAsync();
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        site.Requested.Should().NotContain("https://news.test/private/secret", "robots.txt disallows it");
        site.Requested.Count(u => u == "https://news.test/robots.txt").Should().Be(1, "robots.txt is kept for a day");
        site.Requested.Should().Contain("https://news.test/2026/10/26/second", "the sitemap's pages are crawled");
        site.Requested.Should().Contain("https://third.test/", "linked sites are found");
        site.FetchTimes("news.test").Zip(site.FetchTimes("news.test").Skip(1)).Should()
            .OnlyContain(pair => pair.Second - pair.First >= TimeSpan.FromSeconds(1), "one site is never fetched faster than its wait");

        var found = await crawler.SearchAsync("MARTA beltline");
        found.Should().ContainSingle().Which.Url.Should().Be("https://news.test/2026/10/26/story", "kept under its canonical address");
        (await crawler.SearchAsync("rain georgia")).Should().ContainSingle();
        (await crawler.ListSitesAsync()).Select(s => s.Host).Should().Contain(["news.test", "third.test"]);
        var status = await crawler.GetStatusAsync();
        status.PagesIndexed.Should().Be(2, "home pages are link lists, not articles");

        var topic = new WatchedTopic { Name = "Transit", Keywords = "MARTA, light rail" };
        app.Db.WatchedTopics.Add(topic);
        await app.Db.SaveChangesAsync();
        (await crawler.MatchTopicsAsync()).Should().Be(1);
        (await crawler.MatchTopicsAsync()).Should().Be(0, "a page goes into a topic's feed once");
        var item = await app.Db.NewsItems.AsNoTracking().SingleAsync();
        item.TopicId.Should().Be(topic.Id);
        item.Source.Should().Be("Web · news.test");
        item.Title.Should().Be("Atlanta approves MARTA expansion");
        item.PublishedAt.Should().Be(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        (await crawler.GetStatusAsync()).TopicMatchesToday.Should().Be(1);

        await crawler.RemoveSiteAsync("news.test");
        (await crawler.SearchAsync("MARTA")).Should().BeEmpty();
        var bad = () => crawler.AddSiteAsync("not a site");
        await bad.Should().ThrowAsync<WebCrawlException>();
    }

    // A small web for the crawler: canned answers by address, and a log of what was asked when.
    private sealed class FakeWeb(TimeProvider clock) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly object _gate = new();
        private readonly List<(string Url, DateTimeOffset At)> _log = [];
        public Dictionary<string, (HttpStatusCode Status, string Type, string Body)> Pages { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Requested { get { lock (_gate) return _log.Select(l => l.Url).ToList(); } }

        public IEnumerable<DateTimeOffset> FetchTimes(string host)
        {
            lock (_gate) return _log.Where(l => new Uri(l.Url).Host == host && !l.Url.EndsWith("/robots.txt", StringComparison.Ordinal)).Select(l => l.At).ToList();
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (_gate) _log.Add((url, clock.GetUtcNow()));
            var (status, type, body) = Pages.TryGetValue(url, out var page) ? page : (HttpStatusCode.NotFound, "text/html", "");
            var response = new HttpResponseMessage(status) { Content = new StringContent(body), RequestMessage = request };
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
            return Task.FromResult(response);
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        private TestApp(SqliteConnection connection, ServiceProvider provider)
        {
            _connection = connection;
            _provider = provider;
            _scope = provider.CreateAsyncScope();
            Db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        }

        public ApplicationDbContext Db { get; }
        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

        public static async Task<TestApp> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
            services.AddScoped<IAppDbContext>(p => p.GetRequiredService<ApplicationDbContext>());
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
            return new TestApp(connection, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
