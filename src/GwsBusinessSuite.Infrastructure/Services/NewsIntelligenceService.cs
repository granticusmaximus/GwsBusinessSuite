using CodeHollow.FeedReader;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsIntelligenceService(
    IAppDbContextFactory dbContextFactory,
    IOllamaService ollama,
    ILogger<NewsIntelligenceService> logger) : INewsIntelligenceService
{
    // Curated RSS feeds by category. No API key needed — all public.
    private static readonly string[] GeneralFeeds =
    [
        "https://feeds.bbci.co.uk/news/rss.xml",
        "https://feeds.reuters.com/reuters/topNews",
        "https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml",
        "https://apnews.com/rss",
    ];

    private static readonly string[] TechFeeds =
    [
        "https://hnrss.org/frontpage",
        "https://feeds.feedburner.com/TechCrunch",
        "https://feeds.arstechnica.com/arstechnica/index",
        "https://www.theverge.com/rss/index.xml",
        "https://www.wired.com/feed/rss",
    ];

    private static readonly string[] BusinessFeeds =
    [
        "https://feeds.a.dj.com/rss/RSSWorldNews.xml",
        "https://feeds.bloomberg.com/markets/news.rss",
    ];

    private static readonly string[] ScienceFeeds =
    [
        "https://rss.sciencedaily.com/top/science.xml",
        "https://news.mit.edu/rss/feed",
    ];

    private static readonly string[] AllFeeds =
        [.. GeneralFeeds, .. TechFeeds, .. BusinessFeeds, .. ScienceFeeds];

    private const int MaxItemsPerTopic = 30;
    private const int MaxTopNewsItems = 25;
    private const int NewsItemTtlHours = 24;
    private const string OllamaModel = "llama3.2";

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

        // Remove associated news items first (no cascade configured)
        var items = await db.NewsItems.Where(n => n.TopicId == id).ToListAsync(ct);
        db.NewsItems.RemoveRange(items);
        db.WatchedTopics.Remove(topic);
        await db.SaveChangesAsync(ct);
    }

    public async Task<NewsFeedResult> GetFeedAsync(Guid? topicId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours);

        var topicMap = await db.WatchedTopics
            .AsNoTracking()
            .ToDictionaryAsync(t => t.Id, ct);

        IQueryable<NewsItem> query = db.NewsItems.AsNoTracking().Where(n => n.FetchedAt >= cutoff);

        if (topicId.HasValue)
            query = query.Where(n => n.TopicId == topicId.Value);

        var items = await query
            .OrderByDescending(n => n.PublishedAt ?? n.FetchedAt)
            .Take(100)
            .ToListAsync(ct);

        var dtos = items.Select(n =>
        {
            var topicName = n.TopicId.HasValue && topicMap.TryGetValue(n.TopicId.Value, out var t) ? t.Name : "Top News";
            var topicColor = n.TopicId.HasValue && topicMap.TryGetValue(n.TopicId.Value, out var tc) ? tc.ColorHex : "#64748b";
            return new NewsItemDto(n.Id, n.TopicId, topicName, topicColor, n.Title, n.Url, n.Source,
                n.PublishedAt, n.Description, n.OllamaSummary, n.FetchedAt);
        }).ToList();

        var lastRefresh = items.Count > 0 ? items.Max(n => n.FetchedAt) : (DateTimeOffset?)null;

        return new NewsFeedResult(dtos, lastRefresh);
    }

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
            logger.LogWarning("Topic {Name} has no keywords — skipping refresh", topic.Name);
            return;
        }

        logger.LogInformation("Refreshing topic '{Name}' with keywords: {Keywords}", topic.Name, string.Join(", ", keywords));

        var articles = await FetchFromFeedsAsync(AllFeeds, keywords, ct);
        await PruneAndSaveItemsAsync(db, topicId, articles, MaxItemsPerTopic, ct);

        topic.LastFetchedAt = DateTimeOffset.UtcNow;
        topic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RefreshTopNewsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        logger.LogInformation("Refreshing top news");

        var articles = await FetchFromFeedsAsync(GeneralFeeds, keywords: null, ct);
        await PruneAndSaveItemsAsync(db, topicId: null, articles, MaxTopNewsItems, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshTopNewsAsync(ct);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var activeTopicIds = await db.WatchedTopics
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var id in activeTopicIds)
        {
            try
            {
                await RefreshTopicAsync(id, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh topic {TopicId}", id);
            }
        }

        // Prune expired items (older than 24h) across all topics
        await using var pruneDb = await dbContextFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-NewsItemTtlHours);
        var expired = await pruneDb.NewsItems.Where(n => n.FetchedAt < cutoff).ToListAsync(ct);
        if (expired.Count > 0)
        {
            pruneDb.NewsItems.RemoveRange(expired);
            await pruneDb.SaveChangesAsync(ct);
            logger.LogInformation("Pruned {Count} expired news items", expired.Count);
        }
    }

    private async Task<List<RawArticle>> FetchFromFeedsAsync(string[] feedUrls, string[]? keywords, CancellationToken ct)
    {
        var results = new List<RawArticle>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var url in feedUrls)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var feed = await FeedReader.ReadAsync(url, ct);
                foreach (var item in feed.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.Link) || !seenUrls.Add(item.Link))
                        continue;

                    var title = item.Title ?? string.Empty;
                    var desc = item.Description ?? string.Empty;
                    var combined = $"{title} {desc}";

                    // If no keywords, accept all (used for Top News)
                    var matches = keywords is null || keywords.Any(k =>
                        combined.Contains(k, StringComparison.OrdinalIgnoreCase));

                    if (!matches) continue;

                    results.Add(new RawArticle(
                        title,
                        item.Link!,
                        feed.Title ?? ExtractDomain(url),
                        item.PublishingDate,
                        StripHtml(desc)));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read RSS feed: {Url}", url);
            }
        }

        return results
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private async Task PruneAndSaveItemsAsync(
        IAppDbContext db,
        Guid? topicId,
        List<RawArticle> articles,
        int maxItems,
        CancellationToken ct)
    {
        // Remove stale items for this slot
        var stale = await db.NewsItems
            .Where(n => n.TopicId == topicId)
            .ToListAsync(ct);
        db.NewsItems.RemoveRange(stale);

        var toSave = articles.Take(maxItems).ToList();

        // Batch-summarize with Ollama if we have items
        var summaries = await BatchSummarizeAsync(toSave, ct);

        for (var i = 0; i < toSave.Count; i++)
        {
            var a = toSave[i];
            db.NewsItems.Add(new NewsItem
            {
                TopicId = topicId,
                Title = Truncate(a.Title, 500),
                Url = a.Url,
                Source = Truncate(a.Source, 200),
                PublishedAt = a.PublishedAt,
                Description = Truncate(a.Description, 1000),
                OllamaSummary = summaries.ElementAtOrDefault(i) ?? string.Empty,
                FetchedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<string>> BatchSummarizeAsync(List<RawArticle> articles, CancellationToken ct)
    {
        if (articles.Count == 0) return [];

        try
        {
            var numbered = articles
                .Select((a, i) => $"{i + 1}. {a.Title}: {Truncate(a.Description, 200)}")
                .ToList();

            var userPrompt = string.Join("\n", numbered);

            var systemPrompt =
                "You are a news curator. For each numbered article, respond with one sharp, opinionated sentence " +
                "under 20 words that captures what matters. Respond only with a numbered list matching the input " +
                "order. No intro, no closing remarks.";

            var raw = await ollama.GenerateAsync(OllamaModel, systemPrompt, userPrompt, ct);

            return ParseNumberedList(raw, articles.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ollama summarization failed — storing items without summaries");
            return Enumerable.Repeat(string.Empty, articles.Count).ToList();
        }
    }

    private static List<string> ParseNumberedList(string raw, int expectedCount)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string>(expectedCount);

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            // Strip leading "1. " or "1) "
            var dotIdx = trimmed.IndexOf('.');
            var parenIdx = trimmed.IndexOf(')');
            var sepIdx = dotIdx >= 0 && (parenIdx < 0 || dotIdx < parenIdx) ? dotIdx : parenIdx;

            if (sepIdx > 0 && int.TryParse(trimmed[..sepIdx], out _))
                results.Add(trimmed[(sepIdx + 1)..].Trim());
        }

        // Pad if Ollama returned fewer lines than expected
        while (results.Count < expectedCount)
            results.Add(string.Empty);

        return results;
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
        string Description);
}
