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
}

public sealed record SentinelAiRunView(
    Guid Id, Guid? WikiPageId, string Action, string Instruction, string Output,
    string Status, string Model, string RequestedBy, DateTimeOffset CreatedAt);

public interface ISentinelAiService
{
    Task<SentinelAiRunView> RunAsync(Guid? wikiPageId, string action, string instruction, string performedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelAiRunView>> ListRunsAsync(Guid? wikiPageId, int maxResults = 20, CancellationToken cancellationToken = default);
    Task ReviewAsync(Guid runId, bool approved, string performedBy, CancellationToken cancellationToken = default);
}
