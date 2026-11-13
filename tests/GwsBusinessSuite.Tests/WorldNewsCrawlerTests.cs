using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News trust plan, Phase 1 (13-11-26): polite, adaptive polling (1.5) and news sitemaps (1.6).
public sealed class WorldNewsCrawlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"wn-crawl-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }) if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Schedule_ShouldPauseRefusals_BackOffFailures_AndSlowQuietFeeds()
    {
        var feed = new FeedHealth("https://x.example/rss");

        var refused = FeedSchedule.AfterFailure(feed, 403, "HTTP 403", Now);
        refused.SkipUntil.Should().Be(Now + TimeSpan.FromDays(1), "a site that refuses the server is left alone for a day");
        FeedSchedule.ShouldRead(refused, Now.AddHours(23)).Should().BeFalse();
        FeedSchedule.ShouldRead(refused, Now.AddDays(1)).Should().BeTrue();

        var once = FeedSchedule.AfterFailure(feed, 503, "HTTP 503", Now);
        once.SkipUntil.Should().BeNull("one failure is retried on the next read");
        var twice = FeedSchedule.AfterFailure(once, null, "timeout", Now);
        twice.SkipUntil.Should().Be(Now.AddMinutes(5));
        var many = Enumerable.Range(0, 10).Aggregate(twice, (h, _) => FeedSchedule.AfterFailure(h, 500, "HTTP 500", Now));
        many.SkipUntil.Should().Be(Now.AddHours(1), "backoff stops at an hour");

        var quiet = Enumerable.Range(0, 3).Aggregate(feed, (h, _) => FeedSchedule.AfterSuccess(h, 200, 0, null, null, Now));
        quiet.SkipUntil.Should().Be(Now.AddMinutes(5));
        var veryQuiet = Enumerable.Range(0, 60).Aggregate(quiet, (h, _) => FeedSchedule.AfterSuccess(h, 304, 0, null, null, Now));
        veryQuiet.SkipUntil.Should().Be(Now.AddMinutes(30), "a quiet feed is still read every 30 minutes");
        var busy = FeedSchedule.AfterSuccess(veryQuiet, 200, 4, "\"v2\"", null, Now);
        busy.SkipUntil.Should().BeNull("it comes back to every read the moment it has news");
        busy.Failures.Should().Be(0);
        busy.Added.Should().Be(4);
        busy.ETag.Should().Be("\"v2\"");
    }

    [Fact]
    public void Sitemap_ShouldReadNewsEntries_FromTheLastTwoDays_AndListAnIndexsChildren()
    {
        var sitemap = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:news="http://www.google.com/schemas/sitemap-news/0.9">
              <url><loc>https://www.dw.com/en/a-1</loc><news:news><news:publication><news:name>DW</news:name><news:language>en</news:language></news:publication>
                <news:publication_date>{Now.AddHours(-2):O}</news:publication_date><news:title>US disrupts North Korea&apos;s crypto scam</news:title></news:news></url>
              <url><loc>https://www.dw.com/en/old</loc><news:news><news:publication_date>{Now.AddDays(-3):O}</news:publication_date><news:title>Old story</news:title></news:news></url>
              <url><loc>https://www.dw.com/en/plain-page</loc></url>
            </urlset>
            """;
        var outlet = new NewsOutlet("DW", "English", "Germany", "https://www.dw.com/en/news-sitemap.xml", "sitemap");

        var articles = OutletFeeds.Parse(new MemoryStream(Encoding.UTF8.GetBytes(sitemap)), outlet, Now);

        articles.Should().ContainSingle();
        articles[0].Title.Should().Be("US disrupts North Korea's crypto scam");
        articles[0].SeenAt.Should().Be(Now.AddHours(-2));
        articles[0].Url.Should().Be("https://www.dw.com/en/a-1");

        var index = OutletFeeds.Load(new MemoryStream(Encoding.UTF8.GetBytes("""
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.bbc.com/sitemaps/news-1.xml</loc></sitemap>
              <sitemap><loc>http://insecure.example/news.xml</loc></sitemap>
            </sitemapindex>
            """)));
        OutletFeeds.SitemapChildren(index).Should().Equal("https://www.bbc.com/sitemaps/news-1.xml");
    }

    [Fact]
    public async Task Pipeline_ShouldAskConditionally_AndLeaveARefusingFeedAloneForADay()
    {
        var store = new WorldNewsStore(_dbPath);
        var outlet = new NewsOutlet("Example", "English", "Turkey", "https://www.example.com/rss");
        var requests = new List<HttpRequestMessage>();
        var status = HttpStatusCode.OK;
        var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host != "www.example.com") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            requests.Add(request);
            if (request.Headers.IfNoneMatch.Count > 0 && status == HttpStatusCode.OK) return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? Rss : "", Encoding.UTF8) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        }));
        var time = new Clock(Now);
        var pipeline = new WorldNewsPipeline(store, http, new GdeltDocClient(http, time), new OllamaClient(new Uri("http://localhost:11434/"), http),
            time, new WorldNewsOptions { Editions = [], Outlets = [outlet] });

        (await pipeline.RunCycleAsync(CancellationToken.None)).Added.Should().Be(1);
        store.FeedHealth()[outlet.Url].ETag.Should().Be("\"v1\"");

        time.Now = Now.AddMinutes(5);
        (await pipeline.RunCycleAsync(CancellationToken.None)).Added.Should().Be(0);
        requests.Last().Headers.IfNoneMatch.Should().ContainSingle().Which.Tag.Should().Be("\"v1\"");
        store.FeedHealth()[outlet.Url].LastStatus.Should().Be(304);

        status = HttpStatusCode.Forbidden;
        time.Now = Now.AddMinutes(10);
        await pipeline.RunCycleAsync(CancellationToken.None);
        store.FeedHealth()[outlet.Url].SkipUntil.Should().Be(Now.AddMinutes(10).AddDays(1));
        var asked = requests.Count;
        time.Now = Now.AddHours(2);
        (await pipeline.RunCycleAsync(CancellationToken.None)).Notes.Should().Contain("1 quiet or paused feeds skipped this read.");
        requests.Count.Should().Be(asked, "a refusing site isn't asked again for a day");
    }

    private const string Rss = """
        <rss version="2.0"><channel><title>Example</title>
          <item><title>Floods in Istanbul</title><link>https://www.example.com/floods</link><pubDate>Thu, 13 Nov 2026 08:00:00 GMT</pubDate>
            <description>Heavy rain flooded streets and homes in Istanbul on Thursday.</description></item>
        </channel></rss>
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
