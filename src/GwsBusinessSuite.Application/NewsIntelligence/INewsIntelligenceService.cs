namespace GwsBusinessSuite.Application.NewsIntelligence;

public interface INewsIntelligenceService
{
    Task<IReadOnlyList<WatchedTopicSummary>> ListTopicsAsync(CancellationToken ct = default);
    Task<WatchedTopicSummary> CreateTopicAsync(string name, string keywords, string colorHex, CancellationToken ct = default);
    Task<WatchedTopicSummary> UpdateTopicAsync(Guid id, string name, string keywords, string colorHex, bool isActive, CancellationToken ct = default);
    Task DeleteTopicAsync(Guid id, CancellationToken ct = default);

    Task<NewsFeedResult> GetFeedAsync(Guid? topicId, CancellationToken ct = default);

    Task RefreshTopicAsync(Guid topicId, CancellationToken ct = default);
    Task RefreshTopNewsAsync(CancellationToken ct = default);
    Task RefreshAllAsync(CancellationToken ct = default);
}

public sealed record WatchedTopicSummary(
    Guid Id,
    string Name,
    string Keywords,
    string ColorHex,
    bool IsActive,
    DateTimeOffset? LastFetchedAt,
    int UnreadCount);

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
    DateTimeOffset FetchedAt);

public sealed record NewsFeedResult(
    IReadOnlyList<NewsItemDto> Items,
    DateTimeOffset? LastRefreshedAt);
