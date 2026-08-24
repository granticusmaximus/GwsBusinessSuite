using System.Text.Json;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// The native tab's tool set is permanently, structurally read-only: search_wiki and get_page,
// nothing else. There is no "modify current page" tool here - unlike the hosted chat loop's
// propose_* write tools, a standalone chat tab has no "currently open page" to attach a mutation
// to and no review UI for one. Enforced at two layers: no mutating tool exists in this class,
// and the sentinel:read scope a key is issued under has no sentinel:write counterpart, so even a
// future mutating tool couldn't be authorized by a key generated for this feature.
public sealed class NativeToolExecutor(SentinelGroundingClient grounding) : IOllamaToolExecutor
{
    // Empty until the backend's sentinel:read endpoints actually ship - offering these tools to
    // the model before then would just mean every attempt silently fails (SentinelGroundingClient
    // has no endpoint to call yet). ExecuteAsync's dispatch below is already correct and ready;
    // replacing this empty array with the real tool list (search_wiki, get_page - see
    // ExecuteAsync's switch for their exact shape) is the last step of wiring up backend
    // grounding, not a signature or dispatch-logic change.
    public IReadOnlyList<OllamaToolDefinition> Definitions { get; } = [];

    public async Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            return call.Name switch
            {
                "search_wiki" => await SearchWikiAsync(arguments.RootElement, cancellationToken),
                "get_page" => await GetPageAsync(arguments.RootElement, cancellationToken),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {call.Name}" })
            };
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> SearchWikiAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = arguments.TryGetProperty("query", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        if (query.Length == 0) return JsonSerializer.Serialize(new { error = "A query is required." });

        var results = await grounding.SearchWikiAsync(query, cancellationToken);
        return JsonSerializer.Serialize(results.Select(r => new { r.Id, r.IsDatabase, r.Title, r.Preview }));
    }

    private async Task<string> GetPageAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var pageIdText = arguments.TryGetProperty("pageId", out var value) ? value.GetString() : null;
        if (!Guid.TryParse(pageIdText, out var pageId))
            return JsonSerializer.Serialize(new { error = "pageId must be a valid page id from a prior search_wiki result." });

        var page = await grounding.GetPageAsync(pageId, cancellationToken);
        return page is null
            ? JsonSerializer.Serialize(new { error = "Page not found, or not accessible with the configured API key." })
            : JsonSerializer.Serialize(new { page.Id, page.Title, page.Content });
    }
}
