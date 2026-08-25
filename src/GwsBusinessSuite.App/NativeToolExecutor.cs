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
    // Text matches SentinelAiService.BuildToolCallingTools()'s search_wiki/get_page definitions
    // verbatim, so the model gets the same descriptions whether it's talking to the hosted loop
    // or this native one. If no grounding key is configured, SentinelGroundingClient's calls
    // return empty/null rather than erroring, so offering these tools is safe even then - the
    // model just learns search comes back empty and answers from its own knowledge instead.
    public IReadOnlyList<OllamaToolDefinition> Definitions { get; } =
    [
        new OllamaToolDefinition(
            "search_wiki",
            "Search Sentinel wiki pages and databases by keyword. Returns up to 5 ranked matches, each with id, title, and a short preview.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Search keywords"}},"required":["query"]}"""),
        new OllamaToolDefinition(
            "get_page",
            "Fetch the full plain-text content of one Sentinel wiki page by its id (a GUID, usually taken from a prior search_wiki result).",
            """{"type":"object","properties":{"pageId":{"type":"string","description":"The page's GUID id"}},"required":["pageId"]}""")
    ];

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
