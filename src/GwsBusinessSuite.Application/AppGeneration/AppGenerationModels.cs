using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Application.AppGeneration;

// One proposed page in a chat's current draft - Layout mirrors CmsPage.BlocksJson's real
// schema (PageLayout) so approval can hand it straight to ICmsBuilderService.SavePageAsync
// with no translation step.
public sealed record GeneratedPageSpec(string Title, string Slug, string MetaDescription, PageLayout Layout);

public sealed record AppGenerationMessageView(string Role, string Content, DateTimeOffset CreatedAt);

public sealed class AppGenerationRequestView
{
    public Guid Id { get; init; }
    public Guid TargetSiteId { get; init; }
    public string TargetSiteName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<GeneratedPageSpec> GeneratedPages { get; init; } = [];
    public IReadOnlyList<AppGenerationMessageView> Messages { get; init; } = [];
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? RejectedAt { get; init; }
    public string RejectionReason { get; init; } = string.Empty;
}

public sealed record StartAppGenerationInput(Guid TargetSiteId, string Title, string InitialPrompt);

public sealed record AppGenerationChatResult(bool Success, string? ErrorMessage, AppGenerationRequestView? Request);
