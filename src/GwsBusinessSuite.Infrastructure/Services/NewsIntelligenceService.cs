using CodeHollow.FeedReader;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsIntelligenceService(
    IAppDbContextFactory dbContextFactory,
    IOllamaService ollama,
    IOptions<ContentStudioOptions> studioOptions,
    HttpClient http,
    ILogger<NewsIntelligenceService> logger) : INewsIntelligenceService
{
    // Google News RSS is used exclusively because:
    // 1. It works reliably from cloud / datacenter IPs (unlike outlet-specific feeds).
    // 2. It supports keyword search natively, so one request covers all sources.
    // 3. It aggregates hundreds of outlets simultaneously.
    private const string GoogleNewsTopUrl =
        "https://news.google.com/rss?hl=en-US&gl=US&ceid=US:en";

    private const int MaxItemsPerTopic = 30;
    private const int MaxTopNewsItems = 25;
    private const int NewsItemTtlHours = 24;

    private string OllamaModel => studioOptions.Value.Model;

    // ── CRUD ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<WatchedTopicSummary>> ListTopicsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var topics = await db.WatchedTopics
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours);
        var topicIds = topics.Select(t => t.Id).ToList();

        var counts = await db.NewsItems
            .AsNoTracking()
            .Where(n => n.TopicId != null && topicIds.Contains(n.TopicId!.Value) && n.FetchedAt >= cutoff)
            .GroupBy(n => n.TopicId!.Value)
            .Select(g => new { TopicId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var countMap = counts.ToDictionary(c => c.TopicId, c => c.Count);

        return topics.Select(t => new WatchedTopicSummary(
            t.Id, t.Name, t.Keywords, t.ColorHex, t.IsActive, t.LastFetchedAt,
            countMap.GetValueOrDefault(t.Id, 0))).ToList();
    }

    public async Task<WatchedTopicSummary> CreateTopicAsync(string name, string keywords, string colorHex, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var topic = new WatchedTopic
        {
            Name = name.Trim(),
            Keywords = keywords.Trim(),
            ColorHex = colorHex,
            IsActive = true
        };
        db.WatchedTopics.Add(topic);
        await db.SaveChangesAsync(ct);
        return new WatchedTopicSummary(topic.Id, topic.Name, topic.Keywords, topic.ColorHex, topic.IsActive, null, 0);
    }

    public async Task<WatchedTopicSummary> UpdateTopicAsync(Guid id, string name, string keywords, string colorHex, bool isActive, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var topic = await db.WatchedTopics.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Topic {id} not found");

        topic.Name = name.Trim();
        topic.Keywords = keywords.Trim();
        topic.ColorHex = colorHex;
        topic.IsActive = isActive;
        topic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new WatchedTopicSummary(topic.Id, topic.Name, topic.Keywords, topic.ColorHex, topic.IsActive, topic.LastFetchedAt, 0);
    }

    public async Task DeleteTopicAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var topic = await db.WatchedTopics.FindAsync([id], ct);
        if (topic is null) return;

        // ExecuteDeleteAsync avoids loading news items into memory and is safe
        // if a concurrent refresh is touching the same rows simultaneously.
        await db.NewsItems.Where(n => n.TopicId == id).ExecuteDeleteAsync(ct);
        db.WatchedTopics.Remove(topic);
        await db.SaveChangesAsync(ct);
    }

    // ── Feed read ─────────────────────────────────────────────

    public async Task<NewsFeedResult> GetFeedAsync(Guid? topicId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours);
        var topicMap = await db.WatchedTopics.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);

        IQueryable<NewsItem> query = db.NewsItems.AsNoTracking().Where(n => n.FetchedAt >= cutoff);
        if (topicId.HasValue)
            query = query.Where(n => n.TopicId == topicId.Value);

        var items = await query.OrderByDescending(n => n.FetchedAt).Take(100).ToListAsync(ct);

        // Re-sort in memory so published timestamps take precedence over fetch order.
        items = items.OrderByDescending(n => n.PublishedAt ?? n.FetchedAt).ToList();

        var dtos = items.Select(n =>
        {
            var topicName  = n.TopicId.HasValue && topicMap.TryGetValue(n.TopicId.Value, out var t)  ? t.Name     : "Top News";
            var topicColor = n.TopicId.HasValue && topicMap.TryGetValue(n.TopicId.Value, out var tc) ? tc.ColorHex : "#64748b";
            return new NewsItemDto(n.Id, n.TopicId, topicName, topicColor, n.Title, n.Url,
                n.Source, n.PublishedAt, n.Description, n.OllamaSummary, n.FetchedAt);
        }).ToList();

        var lastRefresh = items.Count > 0 ? (DateTimeOffset?)items.Max(n => n.FetchedAt) : null;
        return new NewsFeedResult(dtos, lastRefresh);
    }

    // ── Refresh ───────────────────────────────────────────────

    public async Task RefreshTopicAsync(Guid topicId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var topic = await db.WatchedTopics.FindAsync([topicId], ct);
        if (topic is null || !topic.IsActive) return;

        var keywords = topic.Keywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length > 0)
            .ToArray();

        if (keywords.Length == 0)
        {
            logger.LogWarning("Topic '{Name}' has no keywords — skipping", topic.Name);
            return;
        }

        logger.LogInformation("Refreshing topic '{Name}': {Keywords}", topic.Name, string.Join(", ", keywords));

        var articles = await FetchGoogleNewsAsync(keywords, ct);
        logger.LogInformation("Topic '{Name}': fetched {Count} articles", topic.Name, articles.Count);

        await PruneAndSaveItemsAsync(db, topicId, articles, MaxItemsPerTopic, ct);

        topic.LastFetchedAt = DateTimeOffset.UtcNow;
        topic.UpdatedAt     = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RefreshTopNewsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        logger.LogInformation("Refreshing top news");
        var articles = await FetchGoogleNewsAsync(keywords: null, ct);
        logger.LogInformation("Top news: fetched {Count} articles", articles.Count);
        await PruneAndSaveItemsAsync(db, topicId: null, articles, MaxTopNewsItems, ct);
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshTopNewsAsync(ct);

        List<Guid> activeTopicIds;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            activeTopicIds = await db.WatchedTopics
                .AsNoTracking()
                .Where(t => t.IsActive)
                .Select(t => t.Id)
                .ToListAsync(ct);
        }

        foreach (var id in activeTopicIds)
        {
            if (ct.IsCancellationRequested) break;
            try   { await RefreshTopicAsync(id, ct); }
            catch (Exception ex) { logger.LogError(ex, "Failed to refresh topic {TopicId}", id); }
        }

        // Prune items older than 24h using direct SQL to avoid concurrency collisions.
        await using var pruneDb = await dbContextFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours);
        var pruned = await pruneDb.NewsItems.Where(n => n.FetchedAt < cutoff).ExecuteDeleteAsync(ct);
        if (pruned > 0)
            logger.LogInformation("Pruned {Count} expired news items", pruned);
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

    // ── Ollama summarisation ──────────────────────────────────

    private async Task PruneAndSaveItemsAsync(
        IAppDbContext db,
        Guid? topicId,
        List<RawArticle> articles,
        int maxItems,
        CancellationToken ct)
    {
        await db.NewsItems.Where(n => n.TopicId == topicId).ExecuteDeleteAsync(ct);

        var toSave   = articles.Take(maxItems).ToList();
        var summaries = await BatchSummarizeAsync(toSave, ct);

        for (var i = 0; i < toSave.Count; i++)
        {
            var a = toSave[i];
            db.NewsItems.Add(new NewsItem
            {
                TopicId      = topicId,
                Title        = Truncate(a.Title, 500),
                Url          = a.Url,
                Source       = Truncate(a.Source, 200),
                PublishedAt  = a.PublishedAt,
                Description  = Truncate(a.Description, 1000),
                OllamaSummary = summaries.ElementAtOrDefault(i) ?? string.Empty,
                FetchedAt    = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<string>> BatchSummarizeAsync(List<RawArticle> articles, CancellationToken ct)
    {
        if (articles.Count == 0) return [];
        try
        {
            var input = articles
                .Select((a, i) => $"{i + 1}. {a.Title}: {Truncate(a.Description, 200)}")
                .ToList();

            const string system =
                "You are a news curator. For each numbered article, respond with one sharp, opinionated " +
                "sentence under 20 words that captures what matters. Respond only with a numbered list " +
                "matching the input order. No intro, no closing remarks.";

            var raw = await ollama.GenerateAsync(OllamaModel, system, string.Join("\n", input), ct);
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
        string Description);
}
