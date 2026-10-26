using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.WebCrawl;

public sealed class WebCrawlOptions
{
    public const string SectionName = "WebCrawl";
    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = "/app/data/webcrawl.db";
    // Fetches a day, spread over the day so discovery keeps going around the clock.
    public int DailyPageLimit { get; set; } = 4000;
    public int RetentionDays { get; set; } = 14;
    // The least wait between two fetches from one site (robots.txt's Crawl-delay can ask for more).
    public int MinDelaySeconds { get; set; } = 10;
    // Sites found through links and added to the crawl each day.
    public int MaxNewSitesPerDay { get; set; } = 40;
    // How far into a site from its home page, feed or sitemap.
    public int MaxDepth { get; set; } = 3;
    public int ParallelSites { get; set; } = 6;
    public int StartupDelaySeconds { get; set; } = 120;
}

// Media Watch's web crawler (see IWebCrawlService). Polite by design: it reads each site's
// robots.txt and keeps to it, waits between fetches from one site, names itself in its
// User-Agent, keeps to a daily allowance, and only fetches public addresses on the web ports
// (AutomationDestinationValidator checks every connection, redirects included).
public sealed class WebCrawler(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<WebCrawlOptions> options,
    TimeProvider time,
    ILogger<WebCrawler> logger) : IWebCrawlService
{
    public const string HttpClientName = "webcrawl";
    public const string ProductToken = "GwsBusinessSuite-MediaWatch";
    public const string UserAgent = "Mozilla/5.0 (compatible; GwsBusinessSuite-MediaWatch/1.0; +https://www.gwsapp.net)";
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MinTextLength = 300;
    private const int FeedEverySeconds = 2 * 3600;
    private const int SitemapEverySeconds = 6 * 3600;
    private const int HomeEverySeconds = 6 * 3600;
    private const int FoundHomeEverySeconds = 7 * 86400;
    private static readonly TimeSpan RobotsFor = TimeSpan.FromDays(1);
    // Social networks, link shorteners, app stores and sign-in pages: no articles, and most refuse crawlers.
    private static readonly string[] SkippedHosts =
    [
        "facebook.com", "twitter.com", "x.com", "instagram.com", "linkedin.com", "youtube.com", "youtu.be", "tiktok.com",
        "pinterest.com", "t.co", "bit.ly", "apps.apple.com", "play.google.com", "accounts.google.com", "google.com",
        "whatsapp.com", "wa.me", "t.me", "telegram.me", "reddit.com", "threads.net", "bsky.app", "mastodon.social", "amazon.com"
    ];

    private readonly Lazy<WebCrawlStore> _store = new(() => new WebCrawlStore(options.Value.DatabasePath));
    private WebCrawlStore Store => _store.Value;
    private long NowMs => time.GetUtcNow().ToUnixTimeMilliseconds();
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    private int MinDelayMs => Math.Max(1, options.Value.MinDelaySeconds) * 1000;

    // Where crawling starts: every feed Media Watch and World News already read (topics' trusted
    // feeds, the curated tech blogs, World News's outlets) and those sites' home pages.
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var feeds = OutletFeeds.Outlets.Select(o => o.Url).Concat(CuratedTechBlogFeeds.Feeds.Select(f => f.Url)).ToList();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var trusted = await db.WatchedTopics.AsNoTracking().Select(t => t.TrustedFeedUrls).ToListAsync(ct);
            feeds.AddRange(trusted.SelectMany(t => t.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        var entries = new List<(string, string, bool, int, int, int)>();
        foreach (var feed in feeds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (WebPageReader.Normalize(feed, new Uri("https://localhost/")) is not { } url || url.IsLoopback) continue;
            await Store.EnsureSiteAsync(url.Host, Home(url), NowMs, MinDelayMs);
            entries.Add((url.AbsoluteUri, url.Host, true, FeedEverySeconds, 0, 9));
            entries.Add((Home(url), url.Host, true, HomeEverySeconds, 0, 7));
        }
        await Store.EnqueueAsync(entries, NowMs);
    }

    // One round: up to ParallelSites sites side by side, each fetching its next address. Paced so
    // the day's allowance is spread across the day. Returns how many addresses were taken.
    public async Task<int> CrawlOnceAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var allowance = Math.Min(settings.DailyPageLimit, 100 + (long)(settings.DailyPageLimit * now.TimeOfDay.TotalSeconds / 86400));
        var fetchedToday = await Store.GetCounterAsync(Today, "fetches");
        if (fetchedToday >= allowance) return 0;
        var batch = await Store.TakeNextAsync(NowMs, (int)Math.Min(settings.ParallelSites, allowance - fetchedToday));
        await Task.WhenAll(batch.Select(entry => FetchEntryAsync(entry, ct)));
        return batch.Count;
    }

    private async Task FetchEntryAsync(FrontierEntry entry, CancellationToken ct)
    {
        try
        {
            var url = new Uri(entry.Url);
            if (url.Port is not (80 or 443))
            {
                await Store.MarkFetchedAsync(entry.Url, entry.Host, 0, NowMs, NowMs, "Only the standard web ports are crawled.");
                return;
            }
            var (robots, delayMs, robotsError) = await GetRobotsAsync(url, ct);
            if (robots is null)
            {
                await Store.DeferSiteAsync(entry.Host, NowMs + 3_600_000, robotsError ?? "robots.txt couldn't be read");
                return;
            }
            if (!robots.IsAllowed(url.PathAndQuery))
            {
                await Store.MarkFetchedAsync(entry.Url, entry.Host, 0, NowMs, NowMs + delayMs, null);
                return;
            }
            await Store.AddToCounterAsync(Today, "fetches", 1);
            var fetched = await FetchAsync(url, ct);
            await Store.MarkFetchedAsync(entry.Url, entry.Host, fetched.Status, NowMs, NowMs + delayMs, fetched.Error);
            if (fetched.Body is null) return;
            if (fetched.IsXml) await HandleXmlAsync(fetched.Body, fetched.FinalUrl);
            else await HandleHtmlAsync(fetched.Body, fetched.FinalUrl, entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Web crawl: {Url} failed", entry.Url);
            await Store.MarkFetchedAsync(entry.Url, entry.Host, 0, NowMs, NowMs + MinDelayMs, Truncate(ex.Message, 300));
        }
    }

    // A site's robots.txt, read again once a day. Missing (4xx) means everything may be crawled;
    // unreachable (5xx, no answer) means nothing may, for now (RFC 9309). Its sitemaps are queued.
    private async Task<(RobotsTxt? Robots, int DelayMs, string? Error)> GetRobotsAsync(Uri url, CancellationToken ct)
    {
        var (stored, fetchedMs, storedDelay) = await Store.GetRobotsAsync(url.Host);
        if (stored is not null && fetchedMs is { } at && NowMs - at < RobotsFor.TotalMilliseconds)
            return (RobotsTxt.Parse(stored, ProductToken), Math.Max(storedDelay, MinDelayMs), null);

        var answer = await FetchAsync(new Uri($"{url.Scheme}://{url.Authority}/robots.txt"), ct, robots: true);
        string text;
        if (answer.Status is >= 200 and < 300 && answer.Body is not null) text = answer.Body;
        else if (answer.Status is >= 400 and < 500) text = string.Empty;
        else return (null, 0, answer.Error ?? "robots.txt couldn't be read");

        var robots = RobotsTxt.Parse(text, ProductToken);
        var delayMs = (int)Math.Clamp((robots.CrawlDelaySeconds ?? 0) * 1000, MinDelayMs, 60_000);
        await Store.SaveRobotsAsync(url.Host, text, NowMs, delayMs);
        await Store.EnqueueAsync(robots.Sitemaps.Take(5)
            .Select(s => WebPageReader.Normalize(s, url)).Where(s => s is not null && s.Host == url.Host)
            .Select(s => (s!.AbsoluteUri, url.Host, true, SitemapEverySeconds, 0, 8)), NowMs);
        return (robots, delayMs, null);
    }

    private sealed record Fetched(int Status, string? Body, bool IsXml, Uri FinalUrl, string? Error);

    private async Task<Fetched> FetchAsync(Uri url, CancellationToken ct, bool robots = false)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", robots
                ? "text/plain,*/*;q=0.5"
                : "text/html,application/xhtml+xml,application/xml;q=0.9,application/rss+xml;q=0.9,application/atom+xml;q=0.9,*/*;q=0.5");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return new Fetched(status, null, false, finalUrl, $"HTTP {status}");
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            var isXml = type.Contains("xml", StringComparison.Ordinal);
            var isHtml = type.Contains("html", StringComparison.Ordinal);
            if (!robots && !isXml && !isHtml) return new Fetched(status, null, false, finalUrl, null);
            if (response.Content.Headers.ContentLength > MaxBytes) return new Fetched(status, null, false, finalUrl, "Too large to read");
            await response.Content.LoadIntoBufferAsync(MaxBytes);
            var body = await response.Content.ReadAsStringAsync(ct);
            // Some feeds and sitemaps are served as text/html or text/plain.
            if (!isXml && body.TrimStart().StartsWith("<?xml", StringComparison.Ordinal)) isXml = true;
            return new Fetched(status, body, isXml && !robots, finalUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return new Fetched(0, null, false, url, Truncate(ex.Message, 300));
        }
    }

    // A feed or sitemap: its newest pages (only the last three days' when it dates them) and up to
    // three of the newest further sitemaps. Its pages can be on other sites (a feed host's articles).
    private async Task HandleXmlAsync(string xml, Uri url)
    {
        var list = WebPageReader.ReadXml(xml, url);
        var cutoff = time.GetUtcNow().AddDays(-3);
        var entries = new List<(string, string, bool, int, int, int)>();
        foreach (var (page, _) in list.Pages.Where(p => p.Date is null || p.Date >= cutoff).Take(150))
        {
            if (IsSkipped(page.Host)) continue;
            await Store.EnsureSiteAsync(page.Host, Home(page), NowMs, MinDelayMs);
            entries.Add((page.AbsoluteUri, page.Host, false, 0, 1, 8));
        }
        entries.AddRange(list.Sitemaps.Where(s => s.Url.Host == url.Host).Take(3)
            .Select(s => (s.Url.AbsoluteUri, s.Url.Host, true, SitemapEverySeconds, 0, 8)));
        await Store.EnqueueAsync(entries, NowMs);
    }

    // A page: its same-site links go deeper (up to MaxDepth); other sites are only noted as new
    // sites to look at (their home page, a few a day). Kept and indexed unless it's a hub (home
    // page) or asks not to be (noindex), or has too little text to be an article.
    private async Task HandleHtmlAsync(string html, Uri url, FrontierEntry entry)
    {
        var page = WebPageReader.ReadHtml(html, url);
        var entries = new List<(string, string, bool, int, int, int)>();
        if (entry.Depth < options.Value.MaxDepth)
        {
            var priority = entry.Hub ? 6 : Math.Max(1, 5 - entry.Depth);
            entries.AddRange(page.Links.Where(l => l.Host == url.Host).Select(l => (l.AbsoluteUri, l.Host, false, 0, entry.Depth + 1, priority)));
        }
        var newSitesLeft = options.Value.MaxNewSitesPerDay - await Store.GetCounterAsync(Today, "newsites");
        foreach (var link in page.Links.Where(l => l.Host != url.Host && !IsSkipped(l.Host)).DistinctBy(l => l.Host))
        {
            if (newSitesLeft <= 0) break;
            var host = link.Host;
            if (await Store.SiteKnownAsync(host)) continue;
            var home = Home(link);
            await Store.EnsureSiteAsync(host, home, NowMs, MinDelayMs);
            entries.Add((home, host, true, FoundHomeEverySeconds, 0, 2));
            await Store.AddToCounterAsync(Today, "newsites", 1);
            newSitesLeft--;
        }
        await Store.EnqueueAsync(entries, NowMs);

        if (entry.Hub || page.NoIndex || page.Title is null || page.Text.Length < MinTextLength) return;
        var keep = page.Canonical is { } canonical && canonical.Host == url.Host ? canonical : url;
        if (await Store.AddPageAsync(keep.AbsoluteUri, url.Host, page.Title, page.PublishedAt, time.GetUtcNow().ToUnixTimeSeconds(), page.Text))
            await Store.AddToCounterAsync(Today, "pages", 1);
    }

    // Each active topic's keywords against the pages kept since its last look; a hit goes into the
    // topic's Media Watch feed (source "Web · host"), once per address. A topic's first look only
    // goes back two days.
    public async Task<int> MatchTopicsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var topics = await db.WatchedTopics.Where(t => t.IsActive).ToListAsync(ct);
        var maxId = await Store.MaxPageIdAsync();
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var topic in topics)
        {
            var mark = $"topic:{topic.Id:N}";
            var after = await Store.GetMarkAsync(mark);
            var query = WebSearchQuery.FromKeywords(topic.Keywords);
            if (query is null)
            {
                await Store.SetMarkAsync(mark, maxId);
                continue;
            }
            const int limit = 200;
            var hits = (await Store.SearchAsync(query, limit, after ?? 0, after is null ? now.AddDays(-2).ToUnixTimeSeconds() : 0, oldestFirst: true))
                .Where(h => h.Id <= maxId).ToList();
            var urls = hits.Select(h => h.Url).ToList();
            var known = (await db.NewsItems.Where(n => n.TopicId == topic.Id && urls.Contains(n.Url)).Select(n => n.Url).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var hit in hits.Where(h => !known.Contains(h.Url)).Take(50))
            {
                var published = hit.PublishedUnix is { } p ? DateTimeOffset.FromUnixTimeSeconds(p) : (DateTimeOffset?)null;
                db.NewsItems.Add(new NewsItem
                {
                    TopicId = topic.Id,
                    Title = Truncate(hit.Title, 500),
                    Url = hit.Url,
                    Source = Truncate($"Web · {hit.Host}", 200),
                    PublishedAt = published,
                    PublishedAtUnixSeconds = published?.ToUnixTimeSeconds(),
                    Description = Truncate(hit.Snippet, 1000),
                    FetchedAt = now,
                    FetchedAtUnixSeconds = now.ToUnixTimeSeconds()
                });
                added++;
            }
            await Store.SetMarkAsync(mark, hits.Count < limit ? maxId : hits[^1].Id);
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            await Store.AddToCounterAsync(Today, "matches", added);
        }
        return added;
    }

    public Task<int> PruneAsync()
    {
        var now = time.GetUtcNow();
        return Store.PruneAsync(now.AddDays(-Math.Max(1, options.Value.RetentionDays)).ToUnixTimeSeconds(), now.AddDays(-30).ToUnixTimeMilliseconds());
    }

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        var fts = WebSearchQuery.FromSearch(query) ?? throw new WebCrawlException("Type what to look for.");
        var hits = await Store.SearchAsync(fts, Math.Clamp(limit, 1, 100));
        return hits.Select(h => new WebSearchHit(h.Url, h.Host, h.Title, h.Snippet,
            h.PublishedUnix is { } p ? DateTimeOffset.FromUnixTimeSeconds(p) : null, DateTimeOffset.FromUnixTimeSeconds(h.FetchedUnix))).ToList();
    }

    public async Task<WebCrawlStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var (pages, sites, queued, lastFetchMs) = await Store.GetStatsAsync();
        return new WebCrawlStatus(options.Value.Enabled, pages, sites, (int)await Store.GetCounterAsync(Today, "fetches"),
            options.Value.DailyPageLimit, queued, lastFetchMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            (int)await Store.GetCounterAsync(Today, "matches"), options.Value.RetentionDays);
    }

    public async Task<IReadOnlyList<WebCrawlSite>> ListSitesAsync(int limit = 100, CancellationToken ct = default) =>
        (await Store.ListSitesAsync(Math.Clamp(limit, 1, 500))).Select(ToSite).ToList();

    public async Task<WebCrawlSite> AddSiteAsync(string url, CancellationToken ct = default)
    {
        var text = (url ?? string.Empty).Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (WebPageReader.Normalize(text, new Uri("https://localhost/")) is not { } parsed || !parsed.Host.Contains('.') || parsed.IsLoopback)
            throw new WebCrawlException("Enter a site's address, e.g. example.com.");
        var home = Home(parsed);
        await Store.EnsureSiteAsync(parsed.Host, home, NowMs, MinDelayMs, byHand: true);
        await Store.EnqueueAsync([(home, parsed.Host, true, HomeEverySeconds, 0, 9)], NowMs);
        if (parsed.AbsoluteUri != home) await Store.EnqueueAsync([(parsed.AbsoluteUri, parsed.Host, true, HomeEverySeconds, 0, 9)], NowMs);
        return new WebCrawlSite(parsed.Host, home, true, 0, null, null);
    }

    public Task RemoveSiteAsync(string host, CancellationToken ct = default) => Store.RemoveSiteAsync((host ?? string.Empty).Trim().ToLowerInvariant());

    private static WebCrawlSite ToSite(StoredSite site) => new(site.Host, site.Home, site.ByHand, site.Pages,
        site.LastFetchUnix is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, site.LastError);

    private static string Home(Uri url) => $"{url.Scheme}://{url.Authority}/";

    private static bool IsSkipped(string host) =>
        SkippedHosts.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

// Runs the crawler: seeds every 6 hours, a round every second or so (pacing and each site's wait
// decide what is actually fetched), topic matching every 5 minutes, pruning once a day.
// WebCrawl:Enabled turns it off (off in Development).
public sealed class WebCrawlBackgroundService(WebCrawler crawler, IOptions<WebCrawlOptions> options, TimeProvider time,
    ILogger<WebCrawlBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.StartupDelaySeconds)), time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var lastSeed = DateTimeOffset.MinValue;
        var lastMatch = DateTimeOffset.MinValue;
        var lastPrune = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = time.GetUtcNow();
                if (now - lastSeed > TimeSpan.FromHours(6))
                {
                    await crawler.SeedAsync(stoppingToken);
                    lastSeed = now;
                }
                var taken = await crawler.CrawlOnceAsync(stoppingToken);
                if (now - lastMatch > TimeSpan.FromMinutes(5))
                {
                    var matched = await crawler.MatchTopicsAsync(stoppingToken);
                    if (matched > 0) logger.LogInformation("Web crawl added {Count} topic matches to Media Watch", matched);
                    lastMatch = now;
                }
                if (now - lastPrune > TimeSpan.FromDays(1))
                {
                    await crawler.PruneAsync();
                    lastPrune = now;
                }
                await Task.Delay(TimeSpan.FromSeconds(taken > 0 ? 1 : 5), time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Web crawl round failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
