namespace GwsBusinessSuite.Application.Abstractions;

public interface ICjAffiliateService
{
    Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default);
    Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default);
    Task<CjLinkFetchResult> FetchLinksAsync(CjLinkFetchRequest request, CancellationToken ct = default);

    // Best-effort: queries the same commissions.api.cj.com GraphQL endpoint already used
    // for partner discovery, requesting additional commission-amount fields. CJ's exact
    // field names for these amounts aren't independently verified against live API docs
    // here, so parsing is defensive - a schema mismatch yields an empty result rather
    // than throwing, see CjAffiliateService.FetchCommissionsAsync.
    Task<CjCommissionFetchResult> FetchCommissionsAsync(CjConnectionRequest request, CancellationToken ct = default);
}
public interface IOllamaService
{
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default);

    // Applies an explicit context-window size, guarding against Ollama's stock default (often a
    // small num_ctx like 2048) silently truncating a large prompt with no error surfaced - see
    // SentinelAiService.TryConsultTeacherAsync, the first caller with a large-enough prompt
    // (~18,000 chars of advisory context) to actually risk that. Default implementation ignores
    // numCtx and falls back to the original contract for older integrations/fakes.
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, int numCtx, CancellationToken ct = default) =>
        GenerateAsync(model, systemPrompt, userPrompt, ct);

    // Loads a model into memory without asking it to generate user-visible output. Fakes
    // and integrations that do not manage a local runtime may safely use this no-op default.
    Task WarmModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

    // Yields each response token/fragment as Ollama streams it (NDJSON, one JSON object per
    // line), for callers that want to render partial output live rather than await the full
    // response - see SentinelAiService.StreamAsync for the first caller.
    IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default);

    // Applies a per-request output ceiling without changing background callers or older
    // integrations that only implement the original streaming contract.
    IAsyncEnumerable<string> GenerateStreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken ct = default) =>
        GenerateStreamAsync(model, systemPrompt, userPrompt, ct);

    Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default);
    Task PullModelAsync(string model, CancellationToken ct = default);
    Task DeleteModelAsync(string model, CancellationToken ct = default);

    // Ollama's current batch embedding endpoint. Kept additive with a default implementation
    // so existing test fakes and non-Ollama integrations remain source-compatible.
    Task<IReadOnlyList<float[]>> EmbedAsync(
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This Ollama service implementation does not support embeddings.");

    // Requires a model with image-generation capability (e.g. an installed Z-Image
    // Turbo / FLUX build) - returns raw base64 PNG bytes, no data: URI prefix.
    Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default);

    // Ollama's /api/chat endpoint with tool-calling support (SentinelGptToolCallLoop is the
    // first and, for now, only caller). Deliberately a separate method from GenerateAsync/
    // GenerateStreamAsync above rather than a modification to them - those stay on /api/generate
    // exactly as before, so every existing caller (ai.modelAdvisor, ai.sentinelSynthesize, the
    // main SentinelGPT single-shot flow) is completely unaffected by this addition. The default
    // body means an implementation that never expects to run a tool-calling loop (including
    // every existing test fake of this interface) doesn't need to change to keep compiling.
    Task<OllamaChatResponse> ChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition>? tools = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This Ollama service implementation does not support tool-calling chat.");
}

// role is "system" | "user" | "assistant" | "tool". ToolCallId/Name are only meaningful on a
// "tool" role message (Ollama doesn't emit an id on its own tool_calls, so this is the caller's
// own bookkeeping key - see SentinelGptToolCallLoop). ToolCalls is only meaningful on an
// "assistant" role message that itself requested tool calls (round-tripped back into the next
// request's message history, matching Ollama/OpenAI-style chat transcripts).
public sealed record OllamaChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<OllamaToolCall>? ToolCalls = null);

// Mirrors Ollama's /api/chat "tools" array shape (OpenAI-compatible function-calling schema):
// {"type":"function","function":{"name","description","parameters": <JSON Schema object>}}.
// ParametersJsonSchema is passed through verbatim as a raw JSON Schema string.
public sealed record OllamaToolDefinition(string Name, string Description, string ParametersJsonSchema);

// One requested call from the model, parsed out of message.tool_calls[].function in Ollama's
// response. ArgumentsJson is the raw JSON object Ollama returned for that call's arguments,
// deliberately left unparsed here - the tool dispatcher (not this transport layer) owns
// validating/binding it to a specific tool's expected shape.
public sealed record OllamaToolCall(string Name, string ArgumentsJson);

public sealed record OllamaChatResponse(string Content, IReadOnlyList<OllamaToolCall> ToolCalls);

public sealed record OllamaWebSearchResult(string Title, string Url, string Content);

public interface IOllamaWebSearchService
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<OllamaWebSearchResult>> SearchAsync(
        string query,
        int? maxResults = null,
        CancellationToken ct = default);
    Task<OllamaWebSearchResult> FetchAsync(string url, CancellationToken ct = default);
}
public interface IDockerDeploymentService { Task<string> DeployAsync(string appName, string dockerfilePath, CancellationToken ct = default); }

public sealed record CjConnectionRequest(
    string DeveloperKey,
    string PublisherId,
    string EndpointUrl,
    int MaxResults = 100,
    string? WebsiteId = null);

public sealed record CjConnectionValidationResult(
    bool IsSuccess,
    string Message,
    int PartnerCountPreview);

public sealed record CjPartnerFetchResult(
    IReadOnlyCollection<CjPartnerRecord> Partners,
    string Message,
    bool IsCompleteRoster = false);

public sealed record CjPartnerRecord(
    string AdvertiserId,
    string AdvertiserName,
    string RelationshipStatus,
    string Country,
    string PrimaryCategory,
    string DetailsUrl);

public sealed record CjLinkFetchRequest(
    string DeveloperKey,
    string PublisherId,
    string WebsiteId,
    string AdvertiserId,
    int MaxResults = 100);

public sealed record CjLinkFetchResult(
    IReadOnlyCollection<CjLinkRecord> Links,
    string Message);

public sealed record CjLinkRecord(
    string LinkId,
    string AdvertiserId,
    string AdvertiserName,
    string LinkName,
    string LinkType,
    string Description,
    string ClickUrl,
    string DestinationUrl,
    string PromotionType,
    DateTimeOffset? PromotionEndDate,
    string? ImageUrl = null);

public sealed record CjCommissionFetchResult(
    IReadOnlyCollection<CjCommissionFetchRecord> Commissions,
    string Message,
    bool IsError = false);

public sealed record CjCommissionFetchRecord(
    string ExternalId,
    string AdvertiserId,
    string AdvertiserName,
    string OrderId,
    string ActionStatus,
    decimal SaleAmount,
    decimal CommissionAmount,
    string Currency,
    DateTimeOffset? EventDate,
    DateTimeOffset? PostingDate);
