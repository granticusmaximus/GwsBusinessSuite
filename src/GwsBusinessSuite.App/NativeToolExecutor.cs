using System.Text.Json;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// The native tab's tool set is permanently, structurally read-only. It now spans the wiki, the
// CRM, the CMS and container health, but every one of those is a read: there is no "modify
// current page" tool here - unlike the hosted chat loop's propose_* write tools, a standalone
// chat tab has no "currently open page" to attach a mutation to and no review UI for one.
// Enforced at two layers: no mutating tool exists in this class, and the sentinel:read scope a
// key is issued under has no sentinel:write counterpart, so even a future mutating tool couldn't
// be authorized by a key generated for this feature.
//
// Widening the read surface deliberately did not widen the trust boundary: the same single key
// authorizes all of it, and it stays a key an admin mints for their own machine.
public sealed class NativeToolExecutor(SentinelGroundingClient grounding) : IOllamaToolExecutor
{
    // search_wiki/get_page match SentinelAiService.BuildToolCallingTools()'s definitions
    // verbatim, so the model gets the same descriptions whether it's talking to the hosted loop
    // or this native one. The rest are native-only reads with no hosted counterpart yet.
    private static readonly OllamaToolDefinition[] GwsReadTools =
    [
        new OllamaToolDefinition(
            "search_wiki",
            "Search Sentinel wiki pages and databases by keyword. Returns up to 5 ranked matches, each with id, title, and a short preview.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Search keywords"}},"required":["query"]}"""),
        new OllamaToolDefinition(
            "get_page",
            "Fetch the full plain-text content of one Sentinel wiki page by its id (a GUID, usually taken from a prior search_wiki result).",
            """{"type":"object","properties":{"pageId":{"type":"string","description":"The page's GUID id"}},"required":["pageId"]}"""),

        // Descriptions say what each tool is *for* rather than what it queries, because tool
        // choice is where a small local model most often goes wrong: naming the business
        // question ("how much is in the pipeline") steers far better than naming the table.
        new OllamaToolDefinition(
            "search_crm",
            "Search the CRM for contacts and deals by name, email, company, or deal title. Use for questions about customers, leads, or specific deals.",
            """{"type":"object","properties":{"query":{"type":"string","description":"A name, company, email, or deal title"}},"required":["query"]}"""),
        new OllamaToolDefinition(
            "get_pipeline",
            "Get the sales pipeline totals: deal count and total value per stage, plus open, won and lost totals in USD. Use for \"how is the pipeline doing\" questions. Takes no arguments.",
            """{"type":"object","properties":{}}"""),
        new OllamaToolDefinition(
            "search_cms_pages",
            "Search the public website's CMS pages by title, slug, or meta description. Returns each page's publish status and URL slug.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Page title, slug, or topic"}},"required":["query"]}"""),
        new OllamaToolDefinition(
            "get_system_health",
            "Get current operational health: unread container health alerts and the most recent ones. Use for \"is anything broken/down\" questions. Takes no arguments.",
            """{"type":"object","properties":{}}""")
    ];

    // Offered only when a grounding key actually exists. Without one every call would come back
    // "unavailable", and a small local model that can't tell "this tool is broken" from "try a
    // different query" will happily burn its whole round budget rediscovering that - the observed
    // bug this guards against. Not offering the tool removes the failure mode structurally rather
    // than asking the model not to retry. Read fresh each round (OllamaToolCallingAgent re-reads
    // Definitions per round), so saving a key mid-conversation takes effect on the next message
    // with no agent rebuild. ExecuteAsync still handles the unavailable/error cases distinctly for
    // the narrower window where a key exists but the request fails.
    public IReadOnlyList<OllamaToolDefinition> Definitions => grounding.IsConfigured ? GwsReadTools : [];

    public async Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            return call.Name switch
            {
                "search_wiki" => await SearchWikiAsync(arguments.RootElement, cancellationToken),
                "get_page" => await GetPageAsync(arguments.RootElement, cancellationToken),
                "search_crm" => await SearchCrmAsync(arguments.RootElement, cancellationToken),
                "get_pipeline" => Describe(await grounding.GetPipelineAsync(cancellationToken), "the sales pipeline"),
                "search_cms_pages" => await SearchCmsPagesAsync(arguments.RootElement, cancellationToken),
                "get_system_health" => Describe(await grounding.GetSystemHealthAsync(cancellationToken), "system health"),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {call.Name}" })
            };
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> SearchCrmAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = ReadQuery(arguments);
        return query.Length == 0
            ? JsonSerializer.Serialize(new { error = "A query is required - a person, company, or deal name." })
            : Describe(await grounding.SearchCrmAsync(query, cancellationToken), "the CRM");
    }

    private async Task<string> SearchCmsPagesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = ReadQuery(arguments);
        return query.Length == 0
            ? JsonSerializer.Serialize(new { error = "A query is required - a page title, slug, or topic." })
            : Describe(await grounding.SearchCmsPagesAsync(query, cancellationToken), "website pages");
    }

    private static string ReadQuery(JsonElement arguments) =>
        arguments.TryGetProperty("query", out var value) ? value.GetString() ?? string.Empty : string.Empty;

    // The same three-way translation the wiki tools do by hand, for every newer read. The
    // "don't retry" wording is load-bearing rather than politeness: a small model that can't
    // distinguish "this tool is broken" from "try a different query" will otherwise spend its
    // whole round budget re-asking - the observed failure this guards against.
    private static string Describe<T>(GroundingOutcome<T> outcome, string subject) => outcome.Reason switch
    {
        GroundingOutcomeReason.NotConfigured => JsonSerializer.Serialize(new
        {
            status = "unavailable",
            note = $"Access to {subject} isn't set up on this Mac (no grounding key configured). Don't retry " +
                   "this - say so plainly, and mention the user can add a key in Settings."
        }),
        GroundingOutcomeReason.RequestFailed => JsonSerializer.Serialize(new
        {
            status = "error",
            note = $"Reading {subject} failed right now. Don't retry more than once - if it fails again, say so " +
                   "instead of guessing at the numbers."
        }),
        _ when outcome.Value is null => JsonSerializer.Serialize(new
        {
            status = "empty",
            note = $"No {subject} data came back. Don't retry the same call - say nothing was found."
        }),
        _ => JsonSerializer.Serialize(new { status = "ok", data = outcome.Value })
    };

    private async Task<string> SearchWikiAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = arguments.TryGetProperty("query", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        if (query.Length == 0) return JsonSerializer.Serialize(new { error = "A query is required." });

        var outcome = await grounding.SearchWikiAsync(query, cancellationToken);
        return outcome.Reason switch
        {
            GroundingOutcomeReason.NotConfigured => JsonSerializer.Serialize(new
            {
                status = "unavailable",
                note = "Wiki search isn't set up on this Mac (no grounding key configured). Don't retry this - " +
                       "answer from general knowledge, or mention the user can add a key in Settings if they want wiki grounding."
            }),
            GroundingOutcomeReason.RequestFailed => JsonSerializer.Serialize(new
            {
                status = "error",
                note = "Wiki search failed right now. Don't retry more than once - if it fails again, answer from general knowledge instead."
            }),
            _ => JsonSerializer.Serialize(new
            {
                status = "ok",
                results = outcome.Results.Select(r => new { r.Id, r.IsDatabase, r.Title, r.Preview })
            })
        };
    }

    private async Task<string> GetPageAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var pageIdText = arguments.TryGetProperty("pageId", out var value) ? value.GetString() : null;
        if (!Guid.TryParse(pageIdText, out var pageId))
            return JsonSerializer.Serialize(new { error = "pageId must be a valid page id from a prior search_wiki result." });

        var outcome = await grounding.GetPageAsync(pageId, cancellationToken);
        return outcome.Reason switch
        {
            GroundingOutcomeReason.NotConfigured => JsonSerializer.Serialize(new
            {
                status = "unavailable",
                note = "Wiki search isn't set up on this Mac (no grounding key configured). Don't retry this - answer from general knowledge instead."
            }),
            GroundingOutcomeReason.RequestFailed => JsonSerializer.Serialize(new
            {
                status = "error",
                note = "Fetching that page failed right now. Don't retry more than once - answer from general knowledge instead."
            }),
            _ when outcome.Page is null => JsonSerializer.Serialize(new
            {
                status = "not_found",
                note = "That page id doesn't exist or isn't accessible. Don't retry with the same id - try a different search_wiki query, or answer from general knowledge."
            }),
            _ => JsonSerializer.Serialize(new { status = "ok", outcome.Page.Id, outcome.Page.Title, outcome.Page.Content })
        };
    }
}
