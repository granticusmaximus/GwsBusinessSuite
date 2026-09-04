using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace GwsBusinessSuite.App;

public sealed record GroundingSearchResult(Guid Id, bool IsDatabase, string Title, string Preview);
public sealed record GroundingPage(Guid Id, string Title, string Content);

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
}
