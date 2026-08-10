namespace GwsBusinessSuite.Application.Wiki;

public static class SentinelAiActions
{
    public const string Ask = "ask";
    public const string Summarize = "summarize";
    public const string Rewrite = "rewrite";
    public const string Translate = "translate";
    public const string Research = "research";
    public const string MeetingNotes = "meetingNotes";
    public const string DatabaseAutofill = "databaseAutofill";
    public const string ModelManagement = "modelManagement";
    // StreamToolCallingConversationAsync's own run marker - not part of AllowedActions since
    // that set gates the generic action-dispatch methods (StreamAsync/StreamConversationAsync),
    // which the tool-calling loop doesn't go through.
    public const string Tools = "tools";
}

public static class SentinelGptDefaults
{
    public const string Model = "sentinelgpt";

    // Keep pasted documents below both the configured model context window and the
    // Blazor circuit's bounded inbound-message allowance. This is deliberately a
    // character limit (rather than a token estimate) so the browser and service can
    // enforce the exact same rule before any workspace or model work begins.
    public const int MaxInstructionLength = 32_000;

    // Production Ollama serializes every call in the app through one global lease
    // (OllamaWorkloadScheduler); IOllamaService's HttpClient otherwise defaults to a 2-hour
    // timeout, so an unbounded chat call can hold that lease - and block every other
    // feature's Ollama use - for up to 2 hours. Bounded to a duration a human is actually
    // willing to sit in a chat waiting for, well under the 2-hour HttpClient ceiling.
    //
    // Was 5 - too short in practice. Production runs a 3B model on CPU-only inference (no
    // GPU), where prompt processing alone ran ~18-20 tokens/sec; a single grounded
    // SentinelGPT request's ~6,300-token prompt (system prompt + workspace context +
    // history) took 5+ minutes just to finish prefill, before generation even started,
    // reliably tripping the old 5-minute limit (confirmed against the Ollama server's own
    // request log: a POST /api/generate cancelled by us at 4m49s, still mid-prompt).
    // OllamaTimeoutMinutesOverride (Settings > AI) still overrides this per-site without a
    // restart if 15 minutes still isn't enough for a particular deployment's hardware.
    public const int DefaultTimeoutMinutes = 15;
}

public static class SentinelGptResponseBudgets
{
    public const int Concise = 384;
    public const int Standard = 768;
    public const int Detailed = 1_536;

    public static bool IsSupported(int maxOutputTokens) =>
        maxOutputTokens is Concise or Standard or Detailed;
}

// A workspace search result actually folded into a run's grounding context - see
// SentinelAiService.BuildGroundedContextAsync. TargetId/IsDatabase match
// SentinelSearchResult/SentinelNavigationItem's existing page-or-database pointer shape.
public sealed record SentinelAiCitation(
    Guid? TargetId,
    bool IsDatabase,
    string Title,
    string? Url = null,
    string SourceType = "sentinel");

public sealed record SentinelAiRunView(
    Guid Id, Guid ConversationId, Guid? WikiPageId, string Action, string Instruction, string Output,
    string Status, string Model, string RequestedBy, DateTimeOffset CreatedAt,
    IReadOnlyList<SentinelAiCitation> Citations);

public sealed record SentinelGptConversationView(
    Guid Id,
    string Title,
    string Preview,
    string Model,
    DateTimeOffset UpdatedAt,
    int ExchangeCount);

// One item per streamed fragment; CompletedRun is null until generation finishes, at which
// point Delta is empty and CompletedRun carries the persisted, reviewable run (citations
// included). Keeping both on one record avoids a second round-trip to fetch the saved run
// after the stream ends, and avoids calling Ollama twice (streaming display + a separate
// non-streaming persist call), which could non-deterministically produce different output.
public sealed record SentinelAiStreamChunk(
    string Delta,
    SentinelAiRunView? CompletedRun,
    string? Activity = null);

public sealed record SentinelGptCommandResult(
    bool Handled,
    bool RequiresConfirmation,
    string? ConfirmationPrompt,
    SentinelAiRunView? CompletedRun);

// A single property suggestion for DatabaseAutofill. Value is already resolved to the exact
// storage shape WikiDatabaseService.SaveInlineCellAsync expects for the property's type (e.g.
// a Select/Status option's id rather than its label, comma-joined option ids for MultiSelect) -
// the UI applies it by calling SaveInlineCellAsync directly with this string, the same as if a
// person had typed/picked it themselves. DisplayLabel is what a human reviews before approving.
public sealed record DatabaseAutofillSuggestion(
    Guid PropertyId, string PropertyName, string Value, string DisplayLabel);

public sealed record DatabaseAutofillResult(
    IReadOnlyList<DatabaseAutofillSuggestion> Suggestions,
    IReadOnlyList<string> Warnings);

public interface ISentinelAiService
{
    bool IsInternetConfigured { get; }
    IAsyncEnumerable<SentinelAiStreamChunk> StreamAsync(Guid? wikiPageId, string action, string instruction, string performedBy, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SentinelAiStreamChunk> StreamConversationAsync(Guid conversationId, Guid? wikiPageId, string action, string instruction, string performedBy, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SentinelAiStreamChunk> StreamAgentConversationAsync(
        Guid conversationId,
        Guid? wikiPageId,
        string instruction,
        string performedBy,
        bool includeInternet,
        bool useDeepAnalysis,
        int maxOutputTokens = SentinelGptResponseBudgets.Standard,
        CancellationToken cancellationToken = default);
    // A bounded ReAct-style loop: the model can call search_wiki/get_page (read-only Sentinel
    // lookups) as many times as it needs, seeing each result before deciding whether to call
    // another tool or give a final answer - unlike every other method here, retrieval is
    // model-decided at generation time rather than pre-fetched by C# before the first call.
    // Activity-only chunks (Delta empty, CompletedRun null, Activity set) surface each tool call
    // as it happens; the final chunk carries the answer the same way StreamAsync's does.
    IAsyncEnumerable<SentinelAiStreamChunk> StreamToolCallingConversationAsync(
        Guid conversationId,
        Guid? wikiPageId,
        string instruction,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task<SentinelGptCommandResult> ExecuteModelCommandAsync(
        Guid conversationId,
        string instruction,
        string performedBy,
        bool confirmed,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelAiRunView>> ListRunsAsync(Guid? wikiPageId, int maxResults = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelGptConversationView>> ListConversationsAsync(string requestedBy, int maxResults = 40, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelAiRunView>> ListConversationRunsAsync(Guid conversationId, string requestedBy, CancellationToken cancellationToken = default);
    Task ReviewAsync(Guid runId, bool approved, string performedBy, CancellationToken cancellationToken = default);

    // DatabaseAutofill: unlike every other action above, this returns structured, per-property
    // suggestions for review rather than a streamed text blob - there is no SentinelAiRun to
    // approve/reject here, the caller applies (or discards) each suggestion directly via
    // WikiDatabaseService.SaveInlineCellAsync.
    Task<DatabaseAutofillResult> SuggestDatabaseRowValuesAsync(
        Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default);
}
