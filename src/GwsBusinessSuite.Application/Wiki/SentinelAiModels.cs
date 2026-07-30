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
}

public static class SentinelGptDefaults
{
    public const string Model = "sentinelgpt";

    // Keep pasted documents below both the configured model context window and the
    // Blazor circuit's bounded inbound-message allowance. This is deliberately a
    // character limit (rather than a token estimate) so the browser and service can
    // enforce the exact same rule before any workspace or model work begins.
    public const int MaxInstructionLength = 32_000;
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
}
