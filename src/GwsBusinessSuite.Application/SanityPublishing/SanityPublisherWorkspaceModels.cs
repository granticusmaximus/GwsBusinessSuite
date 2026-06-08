namespace GwsBusinessSuite.Application.SanityPublishing;

public sealed class SanityPublisherWorkspaceSnapshot
{
    public SanityConfigurationStatus Configuration { get; init; } = new();
    public int TotalDrafts { get; init; }
    public IReadOnlyList<SanityPublishingDraftSummary> PublicationQueue { get; init; } = Array.Empty<SanityPublishingDraftSummary>();
    public IReadOnlyList<SanityPublishingDraftSummary> RecentlyPublished { get; init; } = Array.Empty<SanityPublishingDraftSummary>();
}

public sealed class SanityConfigurationStatus
{
    public bool IsReady { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string Dataset { get; init; } = string.Empty;
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentIdPrefix { get; init; } = string.Empty;
    public bool AutoPublishOnApproval { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class SanityPublishingDraftSummary
{
    public Guid DraftId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Topic { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RevisionNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? LastPublishedAt { get; init; }
    public string PublishState { get; init; } = string.Empty;
    public string PublishStateDetail { get; init; } = string.Empty;
    public bool CanPublish { get; init; }
    public int AffiliatePlacementCount { get; init; }
}
