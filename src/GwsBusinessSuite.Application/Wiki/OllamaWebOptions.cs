namespace GwsBusinessSuite.Application.Wiki;

public sealed class OllamaWebOptions
{
    public const string SectionName = "OllamaWeb";
    public const string DefaultBaseUrl = "https://ollama.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string ApiKey { get; init; } = string.Empty;

    // Ollama's hosted web search and fetch are billed, and Grant wants no charges (19-11-26). The
    // key is ignored unless this is set to true on purpose: search runs on the self-hosted SearXNG,
    // pages are fetched by the server itself, and the gws-ai model works on what they find.
    public bool AllowPaidSearch { get; init; }

    // Key-less web search (13-11-26, Grant): a self-hosted SearXNG (on the AI droplet's private
    // address in production). Used unless paid search is allowed; pages are then fetched directly by the
    // server through a client that refuses private addresses.
    public string SearxngUrl { get; init; } = string.Empty;
    public int MaxResults { get; init; } = 5;
}
