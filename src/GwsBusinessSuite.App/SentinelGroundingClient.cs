using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace GwsBusinessSuite.App;

public sealed record GroundingSearchResult(Guid Id, bool IsDatabase, string Title, string Preview);
public sealed record GroundingPage(Guid Id, string Title, string Content);

// Mirrors of the backend's DeveloperApiSentinel* read models. Duplicated here rather than
// referencing GwsBusinessSuite.Application, same cross-boundary reasoning as the two records
// above: this project is a MAUI client and does not take a dependency on the server's
// application layer for a handful of DTOs.
public sealed record GroundingContact(
    Guid Id, string FullName, string? Email, string? Company, string Status, DateTimeOffset? FollowUpDate);
public sealed record GroundingDeal(
    Guid Id, string Title, string Stage, decimal ValueUsd, string? ContactName, DateTimeOffset? ExpectedCloseDate);
public sealed record GroundingCrmResults(
    IReadOnlyList<GroundingContact> Contacts, IReadOnlyList<GroundingDeal> Deals);
public sealed record GroundingPipelineStage(string Stage, int Count, decimal TotalValueUsd);
public sealed record GroundingPipeline(
    IReadOnlyList<GroundingPipelineStage> Stages,
    int OpenCount, decimal OpenValueUsd, decimal WonValueUsd, decimal LostValueUsd);
public sealed record GroundingCmsPage(
    Guid Id, string Title, string Slug, string Status, DateTimeOffset? PublishedAt);
public sealed record GroundingHealthAlert(
    string ContainerName, string Severity, string Message, bool IsRead, DateTimeOffset CreatedAt);
public sealed record GroundingSystemHealth(int UnreadAlertCount, IReadOnlyList<GroundingHealthAlert> RecentAlerts);

// The generic outcome wrapper the newer reads use. The two wiki-specific outcome types below
// predate it and keep their own shapes so existing callers are untouched.
public sealed record GroundingOutcome<T>(GroundingOutcomeReason Reason, T? Value);

// Distinguishes "grounding isn't usable right now" (no key configured, or the request failed)
// from a genuine zero-result search/lookup - callers (NativeToolExecutor) need this to tell a
// tool-calling model "don't retry" versus "this legitimately came back empty," which an empty
// list/null alone can't express.
public enum GroundingOutcomeReason { Success, NotConfigured, RequestFailed }
public sealed record GroundingSearchOutcome(GroundingOutcomeReason Reason, IReadOnlyList<GroundingSearchResult> Results);
public sealed record GroundingPageOutcome(GroundingOutcomeReason Reason, GroundingPage? Page);

// Calls the hosted admin backend's read-only "sentinel:read" developer-API endpoints for wiki
// search/page content - the grounding *data* the native tab's tools need, while inference itself
// stays entirely local (this client never touches Ollama). Native DTOs here rather than
// referencing GwsBusinessSuite.Application for two small records, same cross-boundary reasoning
// as DeepAnalysisAdvisor's duplicated model-name constants.
//
// The actual HTTP calls land with the backend endpoints (new sentinel:read scope + 2 new
// /api/v1/sentinel/* routes) - until then this returns empty/null so a native tab used before a
// key is configured, or before those endpoints ship, degrades to plain ungrounded local chat
// rather than erroring.
public sealed class SentinelGroundingClient(SecureApiKeyStore apiKeyStore)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(AppEndpoints.BaseUrl) };

    // Lets a caller skip offering wiki tools at all when there's no key to authorize them with -
    // a tool the model can't see is one it can't waste rounds retrying.
    public bool IsConfigured => apiKeyStore.HasKey();

    public async Task<GroundingSearchOutcome> SearchWikiAsync(string query, CancellationToken cancellationToken)
    {
        if (await apiKeyStore.GetAsync() is not { Length: > 0 } apiKey)
            return new(GroundingOutcomeReason.NotConfigured, []);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/sentinel/search?query={Uri.EscapeDataString(query)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(GroundingOutcomeReason.RequestFailed, []);
        var results = await response.Content.ReadFromJsonAsync<IReadOnlyList<GroundingSearchResult>>(cancellationToken) ?? [];
        return new(GroundingOutcomeReason.Success, results);
    }

    public async Task<GroundingPageOutcome> GetPageAsync(Guid pageId, CancellationToken cancellationToken)
    {
        if (await apiKeyStore.GetAsync() is not { Length: > 0 } apiKey)
            return new(GroundingOutcomeReason.NotConfigured, null);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sentinel/pages/{pageId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(GroundingOutcomeReason.RequestFailed, null);
        var page = await response.Content.ReadFromJsonAsync<GroundingPage>(cancellationToken);
        return new(GroundingOutcomeReason.Success, page);
    }

    public Task<GroundingOutcome<GroundingCrmResults>> SearchCrmAsync(string query, CancellationToken cancellationToken) =>
        GetAsync<GroundingCrmResults>($"/api/v1/sentinel/crm/search?query={Uri.EscapeDataString(query)}", cancellationToken);

    public Task<GroundingOutcome<GroundingPipeline>> GetPipelineAsync(CancellationToken cancellationToken) =>
        GetAsync<GroundingPipeline>("/api/v1/sentinel/crm/pipeline", cancellationToken);

    public Task<GroundingOutcome<IReadOnlyList<GroundingCmsPage>>> SearchCmsPagesAsync(
        string query, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<GroundingCmsPage>>(
            $"/api/v1/sentinel/cms/search?query={Uri.EscapeDataString(query)}", cancellationToken);

    public Task<GroundingOutcome<GroundingSystemHealth>> GetSystemHealthAsync(CancellationToken cancellationToken) =>
        GetAsync<GroundingSystemHealth>("/api/v1/sentinel/health", cancellationToken);

    // One authenticated GET, with the same three-way outcome the wiki calls use, so every tool
    // can tell "no key" from "request failed" from "genuinely empty" without repeating itself.
    private async Task<GroundingOutcome<T>> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        if (await apiKeyStore.GetAsync() is not { Length: > 0 } apiKey)
            return new(GroundingOutcomeReason.NotConfigured, default);

        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(GroundingOutcomeReason.RequestFailed, default);
        return new(GroundingOutcomeReason.Success, await response.Content.ReadFromJsonAsync<T>(cancellationToken));
    }
}
