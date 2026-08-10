namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelSearchResult(
    Guid Id,
    bool IsDatabase,
    string Title,
    string Preview,
    string MatchKind,
    int Score,
    IReadOnlyList<string> MatchedTerms,
    // Phase 5.3 - lets search UI facet-filter an already-fetched result set by author/date
    // without a second round trip, rather than adding filter parameters to SearchAsync itself.
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record SentinelBacklink(
    Guid SourcePageId,
    string SourcePageTitle,
    string Preview);

// Facet filtering (Phase 5.3) over an already-fetched SearchAsync result set - both the
// sidebar's always-present search box and the Ctrl/Cmd+K command palette (Wiki.razor) call
// this against their own independent result lists rather than adding filter parameters to
// SearchAsync itself, which would mean a second round trip on every filter change.
public static class SentinelSearchFiltering
{
    public const string PastWeek = "week";
    public const string PastMonth = "month";
    public const string PastYear = "year";

    public static IReadOnlyList<SentinelSearchResult> Apply(
        IReadOnlyList<SentinelSearchResult> results,
        string? authorFilter,
        string? dateFilter,
        DateTimeOffset now)
    {
        IEnumerable<SentinelSearchResult> filtered = results;
        if (!string.IsNullOrEmpty(authorFilter))
        {
            filtered = filtered.Where(result => string.Equals(result.CreatedBy, authorFilter, StringComparison.OrdinalIgnoreCase));
        }

        var cutoff = dateFilter switch
        {
            PastWeek => now.AddDays(-7),
            PastMonth => now.AddMonths(-1),
            PastYear => now.AddYears(-1),
            _ => (DateTimeOffset?)null
        };
        if (cutoff is { } cutoffValue)
        {
            filtered = filtered.Where(result => result.CreatedAt >= cutoffValue);
        }

        return filtered.ToList();
    }
}

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

public sealed record SentinelSavedSearchView(
    Guid Id,
    string Query,
    DateTimeOffset CreatedAt);

public interface ISentinelWorkspaceService
{
    Task<IReadOnlyList<SentinelSearchResult>> SearchAsync(
        string query,
        string username,
        int maxResults = 25,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelBacklink>> GetBacklinksAsync(
        Guid targetPageId,
        string username,
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
        string username,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelMention>> GetMentionsAsync(
        string username,
        int maxResults = 20,
        CancellationToken cancellationToken = default);

    // Reuses SentinelBacklink's shape ("which page mentions this, and how") for a database
    // row target instead of a page target - see GetBacklinksAsync's own scan for the pattern.
    Task<IReadOnlyList<SentinelBacklink>> GetRowMentionsAsync(
        Guid wikiDatabaseId,
        Guid rowId,
        string username,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelSavedSearchView>> ListSavedSearchesAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<SentinelSavedSearchView> SaveSearchAsync(
        string username,
        string query,
        CancellationToken cancellationToken = default);

    Task DeleteSavedSearchAsync(
        string username,
        Guid savedSearchId,
        CancellationToken cancellationToken = default);
}
