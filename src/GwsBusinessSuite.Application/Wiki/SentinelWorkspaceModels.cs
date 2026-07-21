namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelSearchResult(
    Guid Id,
    bool IsDatabase,
    string Title,
    string Preview,
    string MatchKind,
    int Score);

public sealed record SentinelBacklink(
    Guid SourcePageId,
    string SourcePageTitle,
    string Preview);

public interface ISentinelWorkspaceService
{
    Task<IReadOnlyList<SentinelSearchResult>> SearchAsync(
        string query,
        int maxResults = 25,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelBacklink>> GetBacklinksAsync(
        Guid targetPageId,
        CancellationToken cancellationToken = default);
}
