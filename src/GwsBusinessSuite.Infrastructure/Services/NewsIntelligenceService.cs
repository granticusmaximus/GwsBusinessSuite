using System.Net.Http.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodeHollow.FeedReader;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartReader;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsIntelligenceService(
    IAppDbContextFactory dbContextFactory,
    IOllamaService ollama,
    IOptions<ContentStudioOptions> studioOptions,
    HttpClient http,
    IMemoryCache cache,
    NewsRefreshState refreshState,
    ILogger<NewsIntelligenceService> logger) : INewsIntelligenceService
{
    // Google News RSS is used exclusively because:
    // 1. It works reliably from cloud / datacenter IPs (unlike outlet-specific feeds).
    // 2. It supports keyword search natively, so one request covers all sources.
    // 3. It aggregates hundreds of outlets simultaneously.
    private const string GoogleNewsTopUrl =
        "https://news.google.com/rss?hl=en-US&gl=US&ceid=US:en";

    // Curated, individually verified live (real 200 + parseable RSS/XML, not a bot-challenge
    // page) as reliable public feeds from major wire-adjacent outlets, spanning US network/cable
    // news, UK, and international coverage - feeds the "Top News" pool alongside Google News so
    // the default view isn't solely dependent on Google's aggregation. AP News and Reuters were
    // tried first and dropped: AP's public feed now sits behind a Cloudflare bot challenge, and
    // Reuters killed its public RSS feeds entirely (both confirmed 403/404 live, not assumed).
    // CNN's feed only answers over plain HTTP (its HTTPS endpoint fails the TLS handshake) -
    // safe here since this is a server-side backend fetch, not a browser page load.
    private const string DefaultTopNewsTrustedFeeds = """
        https://feeds.bbci.co.uk/news/rss.xml
        https://feeds.npr.org/1001/rss.xml
        https://www.theguardian.com/world/rss
        https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml
        https://www.pbs.org/newshour/feeds/rss/headlines
        https://www.aljazeera.com/xml/rss/all.xml
        http://rss.cnn.com/rss/cnn_topstories.rss
        https://abcnews.go.com/abcnews/topstories
        https://www.cbsnews.com/latest/rss/main
        """;

    private const int MaxItemsPerTopic = 30;
    private const int MaxTopNewsItems = 25;
    // IOllamaService's HttpClient carries a 2-hour outer timeout by design (see
    // ContentStudioService's per-call CancellationTokenSource pattern) - this call is also
    // reachable directly from RefreshAllAsync/RefreshTopicAsync/RefreshTopNewsAsync button
    // clicks on NewsIntelligence.razor, not just the background refresh timers, so without
    // its own bound a wedged Ollama could hold the single global OllamaWorkloadScheduler
    // lease for up to 2 hours from a live page click - freezing every AI feature app-wide.
    private static readonly TimeSpan BatchSummarizeTimeout = TimeSpan.FromSeconds(60);
    // Deliberately shorter than the shared HttpClient's default 15s timeout - up to
    // MaxItemsPerTopic (30) of these run in parallel per topic, so one slow/hostile site
    // shouldn't be able to dominate the batch. A missed/short extraction just means that one
    // article's summary falls back to its RSS snippet, not a failed refresh.
    private static readonly TimeSpan ArticleExtractionTimeout = TimeSpan.FromSeconds(8);
    private const int MinExtractedArticleLength = 200;
    private const int NewsItemTtlHours = 24;
    private const int MaxConcurrentRefreshes = 3;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly Meter Meter = new("GwsBusinessSuite.NewsIntelligence", "1.0");
    private static readonly Histogram<double> RefreshDuration = Meter.CreateHistogram<double>(
        "gws.news.refresh.duration", "s", "News refresh stage duration");

    private string OllamaModel => studioOptions.Value.Model;

    // ── CRUD ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<WatchedTopicSummary>> ListTopicsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var topics = await db.WatchedTopics
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var cutoffUnixSeconds = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours).ToUnixTimeSeconds();
        var topicIds = topics.Select(t => t.Id).ToList();

        var recentCounts = await db.NewsItems
            .AsNoTracking()
            .Where(n => n.TopicId != null
                && topicIds.Contains(n.TopicId!.Value)
                && n.FetchedAtUnixSeconds >= cutoffUnixSeconds)
            .GroupBy(n => n.TopicId!.Value)
            .Select(group => new { TopicId = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        var countMap = recentCounts.ToDictionary(x => x.TopicId, x => x.Count);

        return topics.Select(t => new WatchedTopicSummary(
            t.Id, t.Name, t.Keywords, t.ColorHex, t.IsActive, t.LastFetchedAt,
            countMap.GetValueOrDefault(t.Id, 0), t.TopicType, t.TrustedFeedUrls)).ToList();
    }

    public async Task<WatchedTopicSummary> CreateTopicAsync(string name, string keywords, string colorHex, string topicType, string trustedFeedUrls, CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var topic = new WatchedTopic
            {
                Name = name.Trim(),
                Keywords = keywords.Trim(),
                ColorHex = colorHex,
                IsActive = true,
                TopicType = NormalizeTopicType(topicType),
                TrustedFeedUrls = trustedFeedUrls.Trim()
            };
            db.WatchedTopics.Add(topic);
            await db.SaveChangesAsync(ct);
            return new WatchedTopicSummary(topic.Id, topic.Name, topic.Keywords, topic.ColorHex, topic.IsActive, null, 0, topic.TopicType, topic.TrustedFeedUrls);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<WatchedTopicSummary> UpdateTopicAsync(Guid id, string name, string keywords, string colorHex, bool isActive, string topicType, string trustedFeedUrls, CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var topic = await db.WatchedTopics.FindAsync([id], ct)
                ?? throw new InvalidOperationException($"Topic {id} not found");

            topic.Name = name.Trim();
            topic.Keywords = keywords.Trim();
            topic.ColorHex = colorHex;
            topic.IsActive = isActive;
            topic.TopicType = NormalizeTopicType(topicType);
            topic.TrustedFeedUrls = trustedFeedUrls.Trim();
            topic.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return new WatchedTopicSummary(topic.Id, topic.Name, topic.Keywords, topic.ColorHex, topic.IsActive, topic.LastFetchedAt, 0, topic.TopicType, topic.TrustedFeedUrls);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static string NormalizeTopicType(string topicType) =>
        WatchedTopicTypes.All.Contains(topicType) ? topicType : WatchedTopicTypes.General;

    public async Task DeleteTopicAsync(Guid id, CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            await using var transaction = await db.BeginTransactionAsync(ct);
            var topic = await db.WatchedTopics.FindAsync([id], ct);
            if (topic is null) return;

            await db.NewsItems.Where(n => n.TopicId == id).ExecuteDeleteAsync(ct);
            db.WatchedTopics.Remove(topic);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    // ── Feed read ─────────────────────────────────────────────

    public async Task<NewsFeedResult> GetFeedAsync(Guid? topicId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var cutoffUnixSeconds = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours).ToUnixTimeSeconds();
        var topicMap = await db.WatchedTopics.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);

        IQueryable<NewsItem> query = db.NewsItems.AsNoTracking();
        if (topicId.HasValue)
            query = query.Where(n => n.TopicId == topicId.Value);

        var items = await query
            .Where(n => n.FetchedAtUnixSeconds >= cutoffUnixSeconds)
            .OrderByDescending(n => n.PublishedAtUnixSeconds ?? n.FetchedAtUnixSeconds)
            .Take(100)
            .ToListAsync(ct);

        var dtos = items.Select(n =>
        {
            var topic = n.TopicId is { } id && topicMap.TryGetValue(id, out var match) ? match : null;
            var topicName = topic?.Name ?? "Top News";
            var topicColor = topic?.ColorHex ?? "#64748b";
            return new NewsItemDto(n.Id, n.TopicId, topicName, topicColor, n.Title, n.Url,
                n.Source, n.PublishedAt, n.Description, n.OllamaSummary, n.FetchedAt, n.ImageUrl);
        }).ToList();

        var lastRefresh = items.Count > 0 ? (DateTimeOffset?)items.Max(n => n.FetchedAt) : null;
        return new NewsFeedResult(dtos, lastRefresh);
    }

    // ── Refresh ───────────────────────────────────────────────

    public async Task RefreshTopicAsync(Guid topicId, CancellationToken ct = default)
    {
        var workItem = await LoadTopicWorkItemAsync(topicId, ct);
        if (workItem is null) return;

        refreshState.Begin(1);
        try
        {
            await RefreshWorkItemAsync(workItem, ct);
        }
        finally
        {
            refreshState.Finish();
        }
    }

    public async Task RefreshTopNewsAsync(CancellationToken ct = default)
    {
        refreshState.Begin(1);
        try
        {
            await RefreshWorkItemAsync(RefreshWorkItem.TopNews, ct);
        }
        finally
        {
            refreshState.Finish();
        }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var workItems = new List<RefreshWorkItem> { RefreshWorkItem.TopNews };
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            workItems.AddRange(await db.WatchedTopics
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Select(t => new RefreshWorkItem(t.Id, t.Name, t.Keywords, t.TopicType, MaxItemsPerTopic, t.TrustedFeedUrls))
                .ToListAsync(ct));
        }

        refreshState.Begin(workItems.Count);
        var totalTimer = Stopwatch.StartNew();
        try
        {
            await Parallel.ForEachAsync(
                workItems,
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRefreshes, CancellationToken = ct },
                async (workItem, token) => await RefreshWorkItemAsync(workItem, token));

            await WriteLock.WaitAsync(ct);
            try
            {
                await using var pruneDb = await dbContextFactory.CreateDbContextAsync(ct);
                var cutoffUnixSeconds = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours).ToUnixTimeSeconds();
                var pruned = await pruneDb.NewsItems
                    .Where(n => n.FetchedAtUnixSeconds < cutoffUnixSeconds)
                    .ExecuteDeleteAsync(ct);
                if (pruned > 0)
                    logger.LogInformation("Pruned {Count} expired news items", pruned);
            }
            finally
            {
                WriteLock.Release();
            }
        }
        finally
        {
            totalTimer.Stop();
            RefreshDuration.Record(totalTimer.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("stage", "all"));
            logger.LogInformation(
                "News refresh completed in {DurationMs} ms for {WorkItemCount} work items",
                totalTimer.ElapsedMilliseconds, workItems.Count);
            refreshState.Finish();
        }
    }

    public NewsRefreshStatus GetRefreshStatus() => refreshState.Snapshot;

    private async Task<RefreshWorkItem?> LoadTopicWorkItemAsync(Guid topicId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.WatchedTopics
            .AsNoTracking()
            .Where(t => t.Id == topicId && t.IsActive)
            .Select(t => new RefreshWorkItem(t.Id, t.Name, t.Keywords, t.TopicType, MaxItemsPerTopic, t.TrustedFeedUrls))
            .FirstOrDefaultAsync(ct);
    }

    private async Task RefreshWorkItemAsync(RefreshWorkItem workItem, CancellationToken ct)
    {
        var timings = new List<NewsRefreshTiming>();
        refreshState.StartItem(workItem.Name);
        try
        {
            var prepared = await PrepareWorkItemAsync(workItem, timings, ct);
            await CommitPreparedAsync(prepared, timings, ct);
            refreshState.CompleteItem(workItem.Name, timings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh {WorkItem}", workItem.Name);
            refreshState.FailItem(workItem.Name, ex, timings);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
        }
    }

    private async Task<PreparedRefresh> PrepareWorkItemAsync(
        RefreshWorkItem workItem,
        List<NewsRefreshTiming> timings,
        CancellationToken ct)
    {
        var keywords = workItem.TopicId is null
            ? null
            : workItem.Keywords
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => k.Length > 0)
                .ToArray();

        if (workItem.TopicId is not null && keywords is { Length: 0 })
            throw new InvalidOperationException($"Topic '{workItem.Name}' has no keywords.");

        var trustedFeedUrls = workItem.TrustedFeedUrls
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.Length > 0)
            .ToArray();

        var articles = workItem.TopicType == WatchedTopicTypes.Technical
            ? await FetchTechnicalArticlesAsync(keywords!, trustedFeedUrls, workItem.Name, timings, ct)
            : await FetchArticlesAsync(keywords, trustedFeedUrls, workItem.Name, timings, ct);

        var selected = articles.Take(workItem.MaxItems).ToList();

        // Real article text (when it can be fetched) makes for a genuinely grounded summary
        // instead of one built from a 200-character aggregator snippet - see
        // ExtractArticleTextAsync. Per-article failures/timeouts fall back to null, which
        // BatchSummarizeAsync treats the same as "no full text available" today.
        var fullTexts = await MeasureStageAsync(
            workItem.Name, "Article extraction", selected.Count,
            () => Task.WhenAll(selected.Select(a => ExtractArticleTextAsync(a.Url, ct))),
            timings);

        var summaries = await MeasureStageAsync(
            workItem.Name, "Ollama summary", selected.Count,
            () => BatchSummarizeAsync(selected, fullTexts, ct), timings);

        return new PreparedRefresh(workItem, selected, summaries);
    }

    private async Task CommitPreparedAsync(
        PreparedRefresh prepared,
        List<NewsRefreshTiming> timings,
        CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        var timer = Stopwatch.StartNew();
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            await using var transaction = await db.BeginTransactionAsync(ct);

            await db.NewsItems.Where(n => n.TopicId == prepared.WorkItem.TopicId).ExecuteDeleteAsync(ct);
            var fetchedAt = DateTimeOffset.UtcNow;

            for (var i = 0; i < prepared.Articles.Count; i++)
            {
                var article = prepared.Articles[i];
                db.NewsItems.Add(new NewsItem
                {
                    TopicId = prepared.WorkItem.TopicId,
                    Title = Truncate(article.Title, 500),
                    Url = article.Url,
                    Source = Truncate(article.Source, 200),
                    PublishedAt = article.PublishedAt,
                    PublishedAtUnixSeconds = article.PublishedAt?.ToUnixTimeSeconds(),
                    Description = Truncate(article.Description, 1000),
                    OllamaSummary = prepared.Summaries.ElementAtOrDefault(i) ?? string.Empty,
                    ImageUrl = string.IsNullOrWhiteSpace(article.ImageUrl) ? null : Truncate(article.ImageUrl, 1000),
                    FetchedAt = fetchedAt,
                    FetchedAtUnixSeconds = fetchedAt.ToUnixTimeSeconds()
                });
            }

            if (prepared.WorkItem.TopicId is { } topicId)
            {
                var topic = await db.WatchedTopics.FindAsync([topicId], ct);
                if (topic is not null)
                {
                    topic.LastFetchedAt = fetchedAt;
                    topic.UpdatedAt = fetchedAt;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            timer.Stop();
            WriteLock.Release();
            RecordTiming(prepared.WorkItem.Name, "SQLite commit", timer.Elapsed, prepared.Articles.Count, timings);
        }
    }

    // ── Combined sources ─────────────────────────────────────

    /// <summary>
    /// Fetches and merges articles from every source (Google News RSS + dev.to).
    /// When <paramref name="keywords"/> is null, returns each source's general/top feed.
    /// When keywords are provided, runs one search per keyword per source and merges everything.
    /// </summary>
    private async Task<List<RawArticle>> FetchArticlesAsync(
        string[]? keywords,
        string[] trustedFeedUrls,
        string workItem,
        List<NewsRefreshTiming> timings,
        CancellationToken ct)
    {
        var googleTask = MeasureStageAsync(
            workItem, "Google News", 0, () => FetchGoogleNewsAsync(keywords, ct), timings);
        var devToTask = MeasureStageAsync(
            workItem, "dev.to", 0, () => FetchDevToAsync(keywords, ct), timings);
        var trustedFeedsTask = MeasureStageAsync(
            workItem, "Trusted feeds", 0, () => FetchTrustedFeedsAsync(trustedFeedUrls, ct), timings);
        await Task.WhenAll(googleTask, devToTask, trustedFeedsTask);

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RawArticle>();

        foreach (var article in googleTask.Result.Concat(devToTask.Result).Concat(trustedFeedsTask.Result))
        {
            if (seenUrls.Add(article.Url))
                merged.Add(article);
        }

        return merged
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>
    /// Fetches every user-configured trusted RSS feed URL for a topic, in full - unlike the
    /// keyword-driven sources, these are returned unfiltered (same precedent as Google News'
    /// "no keywords -> return the whole top feed" case) since the user picked the feed
    /// specifically, not a search term.
    /// </summary>
    private async Task<List<RawArticle>> FetchTrustedFeedsAsync(string[] feedUrls, CancellationToken ct)
    {
        if (feedUrls.Length == 0) return [];

        var tasks = feedUrls.Select(url => FetchFeedAsync(url, ct)).ToArray();
        var allResults = await Task.WhenAll(tasks);

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RawArticle>();

        foreach (var batch in allResults)
        {
            foreach (var article in batch)
            {
                if (seenUrls.Add(article.Url))
                    merged.Add(article);
            }
        }

        return merged;
    }

    /// <summary>
    /// Technical-topic pipeline: Hacker News + dev.to, deliberately excluding Google News.
    /// Keyword search on Google News is mostly noise for narrow programming terms (e.g.
    /// "Blazor", "C#" mostly return unrelated articles that happen to contain the word),
    /// so technical topics get sources that actually carry developer discussion instead.
    /// Only ever called from RefreshTopicAsync, which already guarantees a non-empty
    /// keyword array (there's no "Technical Top News" concept, unlike the General path) -
    /// so unlike FetchArticlesAsync/FetchDevToAsync, keywords here is never null.
    /// </summary>
    private async Task<List<RawArticle>> FetchTechnicalArticlesAsync(
        string[] keywords,
        string[] trustedFeedUrls,
        string workItem,
        List<NewsRefreshTiming> timings,
        CancellationToken ct)
    {
        var hackerNewsTask = MeasureStageAsync(
            workItem, "Hacker News", 0, () => FetchHackerNewsAsync(keywords, ct), timings);
        var devToTask = MeasureStageAsync(
            workItem, "dev.to", 0, () => FetchDevToAsync(keywords, ct), timings);
        var techBlogsTask = MeasureStageAsync(
            workItem, "Curated blogs", 0, () => FetchCuratedTechBlogsAsync(ct), timings);
        var trustedFeedsTask = MeasureStageAsync(
            workItem, "Trusted feeds", 0, () => FetchTrustedFeedsAsync(trustedFeedUrls, ct), timings);
        await Task.WhenAll(hackerNewsTask, devToTask, techBlogsTask, trustedFeedsTask);

        // Curated blogs have no keyword-search API (unlike HN Algolia / dev.to tags), so
        // match the topic's keywords via case-insensitive substring search against each
        // already-fetched item's title/description instead.
        var matchedTechBlogs = techBlogsTask.Result
            .Where(a => keywords.Any(k =>
                a.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RawArticle>();

        foreach (var article in hackerNewsTask.Result.Concat(devToTask.Result).Concat(matchedTechBlogs).Concat(trustedFeedsTask.Result))
        {
            if (seenUrls.Add(article.Url))
                merged.Add(article);
        }

        return merged
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    // ── Google News RSS ───────────────────────────────────────

    /// <summary>
    /// Fetches articles from Google News RSS.
    /// When <paramref name="keywords"/> is null, returns the top general news feed.
    /// When keywords are provided, runs one search per keyword and merges results.
    /// </summary>
    private async Task<List<RawArticle>> FetchGoogleNewsAsync(string[]? keywords, CancellationToken ct)
    {
        var urls = keywords is null or { Length: 0 }
            ? [GoogleNewsTopUrl]
            : keywords.Select(BuildGoogleSearchUrl).ToArray();

        // Fetch all URLs in parallel (one request per keyword).
        var tasks = urls.Select(url => FetchFeedAsync(url, ct)).ToArray();
        var allResults = await Task.WhenAll(tasks);

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged   = new List<RawArticle>();

        foreach (var batch in allResults)
        {
            foreach (var article in batch)
            {
                if (seenUrls.Add(article.Url))
                    merged.Add(article);
            }
        }

        return merged
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private async Task<List<RawArticle>> FetchFeedAsync(string url, CancellationToken ct)
    {
        try
        {
            var content = await http.GetStringAsync(url, ct);
            var feed    = FeedReader.ReadFromString(content);

            return feed.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Link))
                .Select(item => new RawArticle(
                    item.Title ?? string.Empty,
                    item.Link!,
                    ExtractGoogleNewsSource(item) ?? feed.Title ?? ExtractDomain(url),
                    item.PublishingDate,
                    StripHtml(item.Description ?? string.Empty)))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch feed: {Url}", url);
            return [];
        }
    }

    /// <summary>
    /// Google News RSS encodes the originating publisher in the &lt;source&gt; element.
    /// Fall back to the item title suffix " - Source Name" pattern if needed.
    /// </summary>
    private static string? ExtractGoogleNewsSource(FeedItem item)
    {
        // FeedReader exposes the raw XML element; Google News puts the outlet in
        // the <source> child element of each <item>.
        if (item.SpecificItem is CodeHollow.FeedReader.Feeds.Rss20FeedItem rss)
        {
            var source = rss.Element?.Element("source")?.Value;
            if (!string.IsNullOrWhiteSpace(source)) return source.Trim();
        }

        // Fallback: Google News often appends " - Publisher Name" to the title.
        var title = item.Title ?? string.Empty;
        var dashIdx = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return dashIdx > 0 ? title[(dashIdx + 3)..].Trim() : null;
    }

    private static string BuildGoogleSearchUrl(string keyword) =>
        $"https://news.google.com/rss/search?q={Uri.EscapeDataString(keyword)}&hl=en-US&gl=US&ceid=US:en";

    // ── Hacker News (technical topics only) ──────────────────
    // Algolia's HN Search API - no auth, no documented rate limit, already proven reliable
    // from this app's droplet (TrendResearchService uses the same endpoint for Content
    // Studio's trend research). Far more relevant than Google News keyword search for
    // narrow programming terms, since it searches real developer discussion instead of
    // general news copy that happens to contain the word. No general/front-page fallback
    // here (unlike Google News' top-headlines feed) - there's no "Technical Top News"
    // concept, only per-topic keyword search, so this always takes a non-empty keyword array.
    private async Task<List<RawArticle>> FetchHackerNewsAsync(string[] keywords, CancellationToken ct)
    {
        var urls = keywords.Select(BuildHackerNewsSearchUrl).ToArray();
        var tasks = urls.Select(url => FetchHackerNewsFeedAsync(url, ct)).ToArray();
        var allResults = await Task.WhenAll(tasks);

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RawArticle>();

        foreach (var batch in allResults)
        {
            foreach (var article in batch)
            {
                if (seenUrls.Add(article.Url))
                    merged.Add(article);
            }
        }

        return merged;
    }

    private async Task<List<RawArticle>> FetchHackerNewsFeedAsync(string url, CancellationToken ct)
    {
        try
        {
            var payload = await http.GetFromJsonAsync<HackerNewsResponse>(url, ct);
            if (payload?.Hits is null) return [];

            return payload.Hits
                .Where(hit => !string.IsNullOrWhiteSpace(hit.Title))
                .Select(hit => new RawArticle(
                    hit.Title ?? string.Empty,
                    string.IsNullOrWhiteSpace(hit.Url) ? $"https://news.ycombinator.com/item?id={hit.ObjectId}" : hit.Url!,
                    "Hacker News",
                    DateTimeOffset.TryParse(hit.CreatedAt, out var created) ? created : null,
                    string.Empty))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Hacker News feed: {Url}", url);
            return [];
        }
    }

    private static string BuildHackerNewsSearchUrl(string keyword)
    {
        var twoWeeksAgo = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        return "https://hn.algolia.com/api/v1/search"
            + $"?tags=story&query={Uri.EscapeDataString(keyword)}"
            + $"&numericFilters={Uri.EscapeDataString($"created_at_i>{twoWeeksAgo}")}"
            + "&hitsPerPage=20";
    }

    private sealed record HackerNewsResponse(HackerNewsHit[]? Hits);

    private sealed record HackerNewsHit(
        string? Title,
        string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("objectID")] string? ObjectId,
        [property: System.Text.Json.Serialization.JsonPropertyName("created_at")] string? CreatedAt);

    // ── Curated engineering blogs (Technical topics only) ─────
    // These feeds have no search API, so the whole set is fetched in full and then
    // keyword-matched per topic. Fetched at most once per cache window regardless of how
    // many Technical topics are refreshed in one pass (RefreshAllAsync's topic loop is
    // sequential, so the first Technical topic populates the cache and every subsequent
    // one - including any per-topic "Refresh now" click within the TTL - reuses it,
    // avoiding redundant fetches of the same ~13 feeds).
    private const string TechBlogCacheKey = "news-intel:curated-tech-blogs";
    private static readonly TimeSpan TechBlogCacheDuration = TimeSpan.FromMinutes(15);

    private async Task<List<RawArticle>> FetchCuratedTechBlogsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(TechBlogCacheKey, out List<RawArticle>? cached) && cached is not null)
            return cached;

        var tasks = CuratedTechBlogFeeds.Feeds
            .Select(f => FetchTechBlogFeedAsync(f.Name, f.Url, ct))
            .ToArray();
        var merged = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

        cache.Set(TechBlogCacheKey, merged, TechBlogCacheDuration);
        return merged;
    }

    private async Task<List<RawArticle>> FetchTechBlogFeedAsync(string name, string url, CancellationToken ct)
    {
        try
        {
            var content = await http.GetStringAsync(url, ct);
            var feed = FeedReader.ReadFromString(content);

            return feed.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Link))
                .Select(item => new RawArticle(
                    item.Title ?? string.Empty,
                    item.Link!,
                    name,
                    item.PublishingDate,
                    StripHtml(item.Description ?? string.Empty)))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch curated tech blog feed {Name} ({Url})", name, url);
            return [];
        }
    }

    // ── dev.to ────────────────────────────────────────────────
    // dev.to's public API needs no auth for reading, and unlike Google News RSS it
    // reliably returns a real cover image per article - the source this app's image
    // display actually depends on. Tags are dev.to's only filter axis, so a free-text
    // topic keyword is normalized (lowercased, spaces stripped) and tried as a tag;
    // keywords that aren't real dev.to tags just contribute zero extra articles rather
    // than erroring, same graceful-degradation the Google News per-keyword fetch uses.

    private const string DevToTopUrl = "https://dev.to/api/articles?per_page=25";
    private const int DevToPerKeywordCount = 15;

    private async Task<List<RawArticle>> FetchDevToAsync(string[]? keywords, CancellationToken ct)
    {
        var urls = keywords is null or { Length: 0 }
            ? [DevToTopUrl]
            : keywords.Select(BuildDevToTagUrl).ToArray();

        var tasks = urls.Select(url => FetchDevToFeedAsync(url, ct)).ToArray();
        var allResults = await Task.WhenAll(tasks);

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RawArticle>();
        foreach (var batch in allResults)
        {
            foreach (var article in batch)
            {
                if (seenUrls.Add(article.Url))
                    merged.Add(article);
            }
        }

        return merged;
    }

    private async Task<List<RawArticle>> FetchDevToFeedAsync(string url, CancellationToken ct)
    {
        try
        {
            var articles = await http.GetFromJsonAsync<List<DevToArticleJson>>(url, ct);
            if (articles is null) return [];

            return articles
                .Where(a => !string.IsNullOrWhiteSpace(a.Url))
                .Select(a => new RawArticle(
                    a.Title ?? string.Empty,
                    a.Url!,
                    "dev.to",
                    a.PublishedAt,
                    StripHtml(a.Description ?? string.Empty),
                    a.CoverImage))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch dev.to feed: {Url}", url);
            return [];
        }
    }

    private static string BuildDevToTagUrl(string keyword)
    {
        var tag = DevToTagNormalizer.Normalize(keyword);
        return $"https://dev.to/api/articles?tag={Uri.EscapeDataString(tag)}&per_page={DevToPerKeywordCount}";
    }

    private sealed record DevToArticleJson(
        [property: System.Text.Json.Serialization.JsonPropertyName("title")] string? Title,
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("description")] string? Description,
        [property: System.Text.Json.Serialization.JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("cover_image")] string? CoverImage);

    // ── Ollama summarisation ──────────────────────────────────

    /// <summary>
    /// Fetches an article's own page (via this service's own HttpClient, same as every other
    /// source in this file - keeps the custom User-Agent and, in tests, the RecordingHandler
    /// fixture in control of what's "on the network") and runs the HTML through SmartReader (a
    /// C# port of Mozilla's Readability algorithm - the same one behind Firefox's Reader Mode)
    /// to pull out the real article text, instead of relying solely on a truncated RSS
    /// description. Parsing itself is synchronous/CPU-only once the HTML is in hand, so only the
    /// fetch needs the timeout. Returns null on any failure, timeout, or too-short result -
    /// BatchSummarizeAsync treats that exactly like "no full text available" and falls back to
    /// the RSS snippet for that one article, so a paywalled or slow site never breaks the batch.
    /// </summary>
    private async Task<string?> ExtractArticleTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ArticleExtractionTimeout);
            var html = await http.GetStringAsync(url, timeoutCts.Token);

            // The static Reader.ParseArticle(url, html) overload does not behave as documented
            // for pre-fetched HTML (verified directly - it returns an empty, unreadable Article
            // for genuinely well-formed article markup); the instance constructor + GetArticle()
            // is the form that actually works, confirmed against real sample HTML before relying
            // on it here.
            var article = new Reader(url, html).GetArticle();
            var text = article?.IsReadable == true ? article.TextContent?.Trim() : null;
            return string.IsNullOrWhiteSpace(text) || text.Length < MinExtractedArticleLength ? null : text;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Article extraction skipped for {Url}", url);
            return null;
        }
    }

    private async Task<List<string>> BatchSummarizeAsync(List<RawArticle> articles, string?[] fullTexts, CancellationToken ct)
    {
        if (articles.Count == 0) return [];
        try
        {
            var input = articles
                .Select((a, i) => $"{i + 1}. {a.Title}: {(string.IsNullOrWhiteSpace(fullTexts.ElementAtOrDefault(i)) ? Truncate(a.Description, 200) : Truncate(fullTexts[i]!, 1500))}")
                .ToList();

            const string system =
                "You are a news curator. For each numbered article, respond with one sharp, opinionated " +
                "sentence under 20 words that captures what matters. Respond only with a numbered list " +
                "matching the input order. No intro, no closing remarks.";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(BatchSummarizeTimeout);
            var raw = await ollama.GenerateAsync(OllamaModel, system, string.Join("\n", input), timeoutCts.Token);
            return ParseNumberedList(raw, articles.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ollama summarisation skipped ({Model} unavailable) — articles saved without hot takes", OllamaModel);
            return Enumerable.Repeat(string.Empty, articles.Count).ToList();
        }
    }

    private static List<string> ParseNumberedList(string raw, int expectedCount)
    {
        var lines   = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string>(expectedCount);
        foreach (var line in lines)
        {
            var trimmed  = line.TrimStart();
            var dotIdx   = trimmed.IndexOf('.');
            var parenIdx = trimmed.IndexOf(')');
            var sepIdx   = dotIdx >= 0 && (parenIdx < 0 || dotIdx < parenIdx) ? dotIdx : parenIdx;
            if (sepIdx > 0 && int.TryParse(trimmed[..sepIdx], out _))
                results.Add(trimmed[(sepIdx + 1)..].Trim());
        }
        while (results.Count < expectedCount) results.Add(string.Empty);
        return results;
    }

    // ── Helpers ───────────────────────────────────────────────

    private async Task<T> MeasureStageAsync<T>(
        string workItem,
        string stage,
        int fallbackItemCount,
        Func<Task<T>> action,
        List<NewsRefreshTiming> timings)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await action();
            var itemCount = result is System.Collections.ICollection collection
                ? collection.Count
                : fallbackItemCount;
            timer.Stop();
            RecordTiming(workItem, stage, timer.Elapsed, itemCount, timings);
            return result;
        }
        catch
        {
            timer.Stop();
            RecordTiming(workItem, stage, timer.Elapsed, fallbackItemCount, timings);
            throw;
        }
    }

    private void RecordTiming(
        string workItem,
        string stage,
        TimeSpan elapsed,
        int itemCount,
        List<NewsRefreshTiming> timings)
    {
        var timing = new NewsRefreshTiming(workItem, stage, (long)elapsed.TotalMilliseconds, itemCount);
        lock (timings) timings.Add(timing);
        RefreshDuration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("work_item", workItem),
            new KeyValuePair<string, object?>("stage", stage));
        logger.LogInformation(
            "News refresh {WorkItem} stage {Stage} completed in {DurationMs} ms with {ItemCount} items",
            workItem, stage, timing.DurationMilliseconds, itemCount);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ")
            .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&nbsp;", " ").Replace("&#39;", "'").Replace("&quot;", "\"")
            .Replace("  ", " ").Trim();
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", string.Empty);
        return url;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private sealed record RawArticle(
        string Title,
        string Url,
        string Source,
        DateTimeOffset? PublishedAt,
        string Description,
        string? ImageUrl = null);

    private sealed record RefreshWorkItem(
        Guid? TopicId,
        string Name,
        string Keywords,
        string TopicType,
        int MaxItems,
        string TrustedFeedUrls)
    {
        public static RefreshWorkItem TopNews { get; } = new(
            null, "Top News", string.Empty, WatchedTopicTypes.General, MaxTopNewsItems, DefaultTopNewsTrustedFeeds);
    }

    private sealed record PreparedRefresh(
        RefreshWorkItem WorkItem,
        List<RawArticle> Articles,
        List<string> Summaries);
}
