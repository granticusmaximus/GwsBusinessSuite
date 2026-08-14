namespace GwsBusinessSuite.Application.SeoAudit;

// Achieved/Max rather than a bare pass/fail flag - lets checks that don't apply to every piece
// of content (a keyword check with no keyword supplied, an image-alt check on a page with no
// images) simply not run, and the overall score is a ratio over only the checks that did,
// rather than unfairly capping a page at <100 for a criterion that was never applicable.
public sealed record SeoAuditFinding(string Category, string Status, string Message, int Points, int MaxPoints);

public static class SeoAuditFindingStatuses
{
    public const string Pass = "Pass";
    public const string Warning = "Warning";
    public const string Fail = "Fail";
}

public sealed record SeoAuditResult(
    Guid RunId,
    string ContentType,
    Guid ContentId,
    string ContentTitle,
    int Score,
    IReadOnlyList<SeoAuditFinding> Findings,
    string? AiModel,
    string AiSummary,
    IReadOnlyList<string> AiSuggestions,
    DateTimeOffset RunAt);

public sealed record SeoAuditRunSummary(Guid RunId, int Score, DateTimeOffset RunAt);

public sealed record SeoAuditableContent(Guid Id, string Title, string ContentType);

public interface ISeoAuditService
{
    // Every published/draft Article and CmsPage, for the audit page's content picker.
    Task<IReadOnlyList<SeoAuditableContent>> ListAuditableContentAsync(CancellationToken cancellationToken = default);

    // model is the Ollama model to use for the AI-era readiness pass; null/unreachable skips
    // that pass entirely and the run is scored from the deterministic checklist alone.
    Task<SeoAuditResult> AuditArticleAsync(Guid articleId, string? model, string? primaryKeywordOverride, CancellationToken cancellationToken = default);
    Task<SeoAuditResult> AuditCmsPageAsync(Guid pageId, string? model, string? primaryKeyword, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoAuditRunSummary>> ListRunsAsync(string contentType, Guid contentId, CancellationToken cancellationToken = default);
}
