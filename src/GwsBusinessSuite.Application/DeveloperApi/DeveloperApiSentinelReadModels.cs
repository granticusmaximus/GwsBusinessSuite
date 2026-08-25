namespace GwsBusinessSuite.Application.DeveloperApi;

public sealed record DeveloperApiSentinelSearchResult(Guid Id, bool IsDatabase, string Title, string Preview);

public sealed record DeveloperApiSentinelPage(Guid Id, string Title, string Content);

// The read-only counterpart to ISentinelAiService's search_wiki/get_page tool handlers
// (SentinelAiService.ExecuteToolCallAsync), exposed over the Developer API's sentinel:read scope
// for the native Mac SentinelGPT tab - inference happens locally on the client, only this
// grounding *data* is fetched from the hosted backend.
public interface IDeveloperApiSentinelReadService
{
    Task<IReadOnlyList<DeveloperApiSentinelSearchResult>> SearchWikiAsync(
        string query, string ownerUsername, CancellationToken cancellationToken = default);

    Task<DeveloperApiSentinelPage?> GetPageAsync(
        Guid pageId, string ownerUsername, CancellationToken cancellationToken = default);
}
