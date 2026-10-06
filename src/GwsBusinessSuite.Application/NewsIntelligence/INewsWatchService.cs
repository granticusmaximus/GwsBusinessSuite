namespace GwsBusinessSuite.Application.NewsIntelligence;

// Per-admin reading state, saved articles, Trends, feed discovery, Sentinel clips and the topic
// digest - everything Media Watch layers on top of INewsIntelligenceService's fetched feed.
public interface INewsWatchService
{
    Task<IReadOnlySet<string>> GetReadUrlHashesAsync(string username, CancellationToken ct = default);
    Task MarkReadAsync(string username, IEnumerable<string> urls, CancellationToken ct = default);

    // Unread article counts per topic; Guid.Empty is the shared All News pool.
    Task<IReadOnlyDictionary<Guid, int>> GetUnreadCountsAsync(string username, CancellationToken ct = default);

    Task<IReadOnlyList<SavedArticleView>> ListSavedAsync(string username, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetSavedUrlHashesAsync(string username, CancellationToken ct = default);
    Task SaveArticleAsync(string username, NewsItemDto item, CancellationToken ct = default);
    Task UnsaveArticleAsync(string username, string url, CancellationToken ct = default);

    // Creates a Sentinel page for the article under a shared "Media Watch clips" page, saving the
    // article too. Returns the new page's id (or the existing one if it was clipped before).
    Task<Guid> ClipToSentinelAsync(string username, NewsItemDto item, CancellationToken ct = default);

    Task<NewsTrendsView> GetTrendsAsync(int days = 14, CancellationToken ct = default);

    // Finds RSS/Atom feeds for a site: the URL itself if it is a feed, its <link rel="alternate">
    // feeds, then the usual /feed, /rss.xml... paths.
    Task<IReadOnlyList<DiscoveredFeed>> DiscoverFeedsAsync(string siteUrl, CancellationToken ct = default);

    Task<NewsWatchSettingsView> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(NewsWatchSettingsView settings, CancellationToken ct = default);

    // Sends the topic digest if one is due (or now when force is true). Returns what happened.
    Task<NewsDigestResult> SendDigestAsync(bool force, CancellationToken ct = default);
}

public sealed record SavedArticleView(Guid Id, string Url, string Title, string Source, string TopicName,
    DateTimeOffset? PublishedAt, string Summary, DateTimeOffset SavedAt, Guid? ClippedWikiPageId);

public sealed record DiscoveredFeed(string Url, string Title, int ItemCount);

public sealed record NewsWatchSettingsView(
    int RetentionHours,
    bool DigestEnabled,
    string? DigestRecipient,
    string DigestFrequency,
    int DigestHourLocal,
    int DigestDayOfWeek,
    DateTimeOffset? LastDigestSentAt = null,
    bool CanSendEmail = false);

public sealed record NewsDigestResult(bool Sent, string Message);

public sealed record NewsTrendsView(IReadOnlyList<DateOnly> Days, IReadOnlyList<TopicTrend> Topics);

public sealed record TopicTrend(string TopicKey, string Name, string ColorHex, IReadOnlyList<int> DailyCounts, IReadOnlyList<RisingTerm> RisingTerms);
