namespace GwsBusinessSuite.Application.DeveloperApi;

public sealed record DeveloperApiSentinelSearchResult(Guid Id, bool IsDatabase, string Title, string Preview);

public sealed record DeveloperApiSentinelPage(Guid Id, string Title, string Content);

public sealed record DeveloperApiSentinelContact(
    Guid Id, string FullName, string? Email, string? Company, string Status, DateTimeOffset? FollowUpDate);

public sealed record DeveloperApiSentinelDeal(
    Guid Id, string Title, string Stage, decimal ValueUsd, string? ContactName, DateTimeOffset? ExpectedCloseDate);

public sealed record DeveloperApiSentinelCrmResults(
    IReadOnlyList<DeveloperApiSentinelContact> Contacts,
    IReadOnlyList<DeveloperApiSentinelDeal> Deals);

public sealed record DeveloperApiSentinelPipelineStage(string Stage, int Count, decimal TotalValueUsd);

// Open/won/lost are split out rather than left for the caller to re-derive from Stages: a model
// asked "how's the pipeline?" should not have to know which stage names count as closed.
public sealed record DeveloperApiSentinelPipeline(
    IReadOnlyList<DeveloperApiSentinelPipelineStage> Stages,
    int OpenCount,
    decimal OpenValueUsd,
    decimal WonValueUsd,
    decimal LostValueUsd);

public sealed record DeveloperApiSentinelCmsPageSummary(
    Guid Id, string Title, string Slug, string Status, DateTimeOffset? PublishedAt);

public sealed record DeveloperApiSentinelHealthAlert(
    string ContainerName, string Severity, string Message, bool IsRead, DateTimeOffset CreatedAt);

public sealed record DeveloperApiSentinelSystemHealth(
    int UnreadAlertCount, IReadOnlyList<DeveloperApiSentinelHealthAlert> RecentAlerts);

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

    // The methods below take no ownerUsername, unlike the wiki pair above, and that difference is
    // deliberate rather than an oversight. Wiki pages carry per-page ACLs (ISentinelAccessService),
    // so "who is asking" changes what comes back. CRM records, CMS pages and container health have
    // no per-record ACL in this application at all - the whole admin surface is AdminOnly, and a
    // sentinel:read key can only be minted by an admin for themselves. The scope *is* the gate,
    // so threading an unused username through would imply a filter that doesn't exist.
    //
    // Everything here is read-only by construction. There is no sentinel:write counterpart, so no
    // key issued for this feature can mutate a contact, a deal, a page, or a container.

    Task<DeveloperApiSentinelCrmResults> SearchCrmAsync(
        string query, CancellationToken cancellationToken = default);

    Task<DeveloperApiSentinelPipeline> GetPipelineAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeveloperApiSentinelCmsPageSummary>> SearchCmsPagesAsync(
        string query, CancellationToken cancellationToken = default);

    Task<DeveloperApiSentinelSystemHealth> GetSystemHealthAsync(CancellationToken cancellationToken = default);
}
