using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Research before doing (Grant, 30-10-26: "I want SentinelGPT to research and then do"): what the
// agent reads to get its facts right before it writes a page, a list or a card. App-wide search
// and World News reach business data, so they're Admin only, as the command palette's records and
// the admin-only World News context are. Web search and reading a page send only the query or the
// address out (Ollama's web API, OllamaWeb:ApiKey); without a key those two aren't offered.
public sealed class ResearchAgentTools(
    ISuiteSearchService suiteSearch,
    IOllamaWebSearchService? web = null,
    IWorldNewsSearch? worldNews = null) : ISentinelAgentToolset
{
    private static readonly string[] AdminOnly = [AppRoles.Admin];
    private static readonly string[] ContentRoles = [AppRoles.Admin, AppRoles.Author, AppRoles.Contributor];
    private const int WebPageCharacters = 6_000;
    private const int WebResultCharacters = 600;

    public IEnumerable<ISentinelAgentTool> Tools
    {
        get
        {
            yield return new SentinelAgentTool(
                "search_app",
                "Search everything in the app at once: contacts, companies, deals, support tickets, blog posts, site pages, Sentinel pages, boards and more. Returns titles, what kind each is, and a short description. Use it to research before writing or changing anything.",
                SentinelAgentJson.Schema(("query", "string", "What to look for; 't:contact acme' limits it to one kind", true)),
                SentinelAgentRisk.Read, AdminOnly,
                args => $"Searching the app for \"{args.OptionalText("query")}\"",
                SearchAppAsync);

            if (worldNews is not null)
            {
                yield return new SentinelAgentTool(
                    "search_world_news",
                    "Find what the news is reporting about a topic, place or event, from the World News feeds (many outlets, recent days), with sources.",
                    SentinelAgentJson.Schema(("question", "string", "The topic or question", true)),
                    SentinelAgentRisk.Read, AdminOnly,
                    args => $"Checking World News for \"{args.OptionalText("question")}\"",
                    SearchNewsAsync);
            }

            if (web is { IsConfigured: true })
            {
                yield return new SentinelAgentTool(
                    "search_web",
                    "Search the internet. Returns up to 5 results with title, address and an extract. Use for facts the app doesn't have.",
                    SentinelAgentJson.Schema(("query", "string", "The search", true)),
                    SentinelAgentRisk.Read, ContentRoles,
                    args => $"Searching the web for \"{args.OptionalText("query")}\"",
                    SearchWebAsync);
                yield return new SentinelAgentTool(
                    "read_web_page",
                    "Read one web page's text, by its address (usually one search_web returned).",
                    SentinelAgentJson.Schema(("url", "string", "The page's https address", true)),
                    SentinelAgentRisk.Read, ContentRoles,
                    args => $"Reading {Host(args.OptionalText("url"))}",
                    ReadWebAsync);
            }
        }
    }

    private async Task<SentinelAgentToolResult> SearchAppAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var results = await suiteSearch.SearchAsync(args.Text("query"), context.User.Username, 10, ct);
        return SentinelAgentToolResult.Of(new
        {
            results = results.Select(result => new { title = result.Title, kind = result.Category, about = result.Subtitle, link = result.Url }),
            note = results.Count == 0 ? "Nothing matched. Try other words." : null
        });
    }

    private async Task<SentinelAgentToolResult> SearchNewsAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var found = await worldNews!.ContextAsync(args.Text("question"), ct);
        return found is null
            ? SentinelAgentToolResult.Of(new { found = false, note = "World News has nothing on that in recent days." })
            : SentinelAgentToolResult.Of(new { found = true, reporting = found.Text, sources = found.Sources.Select(source => new { source.Title, source.Url }) });
    }

    private async Task<SentinelAgentToolResult> SearchWebAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var results = await web!.SearchAsync(args.Text("query"), 5, ct);
        return SentinelAgentToolResult.Of(new
        {
            results = results.Select(result => new { title = result.Title, url = result.Url, extract = Limit(result.Content, WebResultCharacters) })
        });
    }

    private async Task<SentinelAgentToolResult> ReadWebAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var url = args.Text("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http"))
            throw new SentinelAgentToolException($"'{url}' isn't a web address.");
        var page = await web!.FetchAsync(address.ToString(), ct);
        return SentinelAgentToolResult.Of(new { title = page.Title, url = page.Url, text = Limit(page.Content, WebPageCharacters) });
    }

    private static string Limit(string text, int characters) => text.Length <= characters ? text : text[..characters] + "…";

    private static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var address) ? address.Host : "a web page";
}
