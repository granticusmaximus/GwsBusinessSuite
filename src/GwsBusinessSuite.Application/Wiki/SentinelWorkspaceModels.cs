namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelSearchResult(
    Guid Id,
    bool IsDatabase,
    string Title,
    string Preview,
    string MatchKind,
    int Score,
    IReadOnlyList<string> MatchedTerms);

public sealed record SentinelBacklink(
    Guid SourcePageId,
    string SourcePageTitle,
    string Preview);

public sealed record SentinelNavigationItem(
    Guid Id,
    bool IsDatabase,
    string Title,
    string? Icon,
    bool IsFavorite,
    DateTimeOffset LastOpenedAt);

public sealed record SentinelNavigationState(
    IReadOnlyList<SentinelNavigationItem> Favorites,
    IReadOnlyList<SentinelNavigationItem> Recents);

public sealed record SentinelMentionSuggestion(
    string Kind,
    string Value,
    string Label,
    string Description);

public sealed record SentinelMention(
    Guid SourcePageId,
    string SourcePageTitle,
    string Preview,
    DateTimeOffset MentionedAt);

public interface ISentinelWorkspaceService
{
    Task<IReadOnlyList<SentinelSearchResult>> SearchAsync(
        string query,
        int maxResults = 25,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelBacklink>> GetBacklinksAsync(
        Guid targetPageId,
        CancellationToken cancellationToken = default);

    Task<SentinelNavigationState> GetNavigationAsync(
        string username,
        int maxRecents = 8,
        CancellationToken cancellationToken = default);

    Task RecordOpenedAsync(
        string username,
        Guid targetId,
        bool isDatabase,
        CancellationToken cancellationToken = default);

    Task<bool> ToggleFavoriteAsync(
        string username,
        Guid targetId,
        bool isDatabase,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelMentionSuggestion>> SearchMentionSuggestionsAsync(
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelMention>> GetMentionsAsync(
        string username,
        int maxResults = 20,
        CancellationToken cancellationToken = default);
}
