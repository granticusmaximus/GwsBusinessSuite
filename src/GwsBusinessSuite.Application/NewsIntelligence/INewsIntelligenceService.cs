namespace GwsBusinessSuite.Application.NewsIntelligence;

public interface INewsIntelligenceService
{
    Task<IReadOnlyList<WatchedTopicSummary>> ListTopicsAsync(CancellationToken ct = default);
    Task<WatchedTopicSummary> CreateTopicAsync(string name, string keywords, string colorHex, string topicType, string trustedFeedUrls, CancellationToken ct = default);
    Task<WatchedTopicSummary> UpdateTopicAsync(Guid id, string name, string keywords, string colorHex, bool isActive, string topicType, string trustedFeedUrls, CancellationToken ct = default);
    Task DeleteTopicAsync(Guid id, CancellationToken ct = default);
    // The software development source pack (TechReadingPack): makes the topic, or tops up its feeds.
    Task<WatchedTopicSummary> AddTechReadingPackAsync(CancellationToken ct = default);

    Task<NewsFeedResult> GetFeedAsync(Guid? topicId, CancellationToken ct = default);

    Task RefreshTopicAsync(Guid topicId, CancellationToken ct = default);
    Task RefreshTopNewsAsync(CancellationToken ct = default);
    Task RefreshAllAsync(CancellationToken ct = default);

    // The release radar: GitHub release feeds of the libraries GWS uses, as a feeds-only topic (20-11-26).
    Task<WatchedTopicSummary> AddReleaseRadarAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("The release radar isn't available here.");

    // Writes AI takes the model was too busy for at refresh time; returns how many (20-11-26).
    Task<int> FillMissingTakesAsync(CancellationToken ct = default) => Task.FromResult(0);
    NewsRefreshStatus GetRefreshStatus();
}

public sealed record WatchedTopicSummary(
    Guid Id,
    string Name,
    string Keywords,
    string ColorHex,
    bool IsActive,
    DateTimeOffset? LastFetchedAt,
    int UnreadCount,
    string TopicType,
    string TrustedFeedUrls,
    string? LastRefreshIssue = null);

public sealed record NewsItemDto(
    Guid Id,
    Guid? TopicId,
    string TopicName,
    string TopicColorHex,
    string Title,
    string Url,
    string Source,
    DateTimeOffset? PublishedAt,
    string Description,
    string OllamaSummary,
    DateTimeOffset FetchedAt,
    string? ImageUrl = null,
    int? CommunityScore = null,
    int? ReadingMinutes = null,
    string? Author = null);

// Meanings: each headline's embedding by item id, when the server's model could give them, so
// differently worded reports of one event group together (20-11-26).
public sealed record NewsFeedResult(
    IReadOnlyList<NewsItemDto> Items,
    DateTimeOffset? LastRefreshedAt,
    IReadOnlyDictionary<Guid, float[]>? Meanings = null);

public sealed record NewsRefreshTiming(
    string WorkItem,
    string Stage,
    long DurationMilliseconds,
    int ItemCount);

public sealed record NewsRefreshStatus(
    bool IsRunning,
    string Phase,
    int CompletedItems,
    int TotalItems,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> ActiveItems,
    IReadOnlyList<NewsRefreshTiming> Timings,
    string? LastError)
{
    public static NewsRefreshStatus Idle { get; } = new(
        false, "Idle", 0, 0, null, null, [], [], null);
}
