using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class ArticleGenerationRequest
{
    public string Topic { get; init; } = string.Empty;
    public string TargetAudience { get; init; } = string.Empty;
    public string PrimaryKeyword { get; init; } = string.Empty;
    public string SecondaryKeywords { get; init; } = string.Empty;
}

public sealed class DraftRevisionRequest
{
    public Guid DraftId { get; init; }
    public string RequestedModifications { get; init; } = string.Empty;
    public string PerformedBy { get; init; } = "content-studio";
}

public sealed class DraftDecisionRequest
{
    public Guid DraftId { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string PerformedBy { get; init; } = "content-studio";
}

public sealed class DraftHeroImageRegenerationRequest
{
    public Guid DraftId { get; init; }
    public string Prompt { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string PerformedBy { get; init; } = "content-studio";
}

public sealed class AffiliatePlacementInteractionRequest
{
    public Guid DraftId { get; init; }
    public string SlotToken { get; init; } = string.Empty;
    public string EventType { get; init; } = AffiliateInteractionEventTypes.Impression;
    public string PerformedBy { get; init; } = "content-studio";
}

public sealed class ArticleGenerationResult
{
    public Guid DraftId { get; init; }
    public string Topic { get; init; } = string.Empty;
    public string TargetAudience { get; init; } = string.Empty;
    public string PrimaryKeyword { get; init; } = string.Empty;
    public string SecondaryKeywords { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Author { get; init; } = "GWS Editorial";
    public string Markdown { get; init; } = string.Empty;
    public string RenderedMarkdown { get; init; } = string.Empty;
    public string PublishMarkdown { get; init; } = string.Empty;
    public string MetaTitle { get; init; } = string.Empty;
    public string MetaDescription { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string CanonicalUrl { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string RequestedModifications { get; init; } = string.Empty;
    public int RevisionNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? RejectedAt { get; init; }
    public IReadOnlyList<ArticleAffiliatePlacementView> AffiliatePlacements { get; init; } = Array.Empty<ArticleAffiliatePlacementView>();
    public ArticleHeroImagePreview HeroImage { get; init; } = ArticleHeroImagePreview.Empty;
    public IReadOnlyList<ContentStudioWorkflowEntry> WorkflowHistory { get; init; } = Array.Empty<ContentStudioWorkflowEntry>();
}

public sealed class ScoredAffiliateOfferView
{
    public string AdvertiserId { get; init; } = string.Empty;
    public string AdvertiserName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string TrackingUrl { get; init; } = string.Empty;
    public double Score { get; init; }
}

public sealed class ArticleAffiliatePlacementView
{
    public string SlotToken { get; init; } = string.Empty;
    public string AdvertiserId { get; init; } = string.Empty;
    public string AdvertiserName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string TrackingUrl { get; init; } = string.Empty;
    public string CallToActionText { get; init; } = "Explore Offer";
    public int SortOrder { get; init; }
    public int Impressions { get; init; }
    public int Clicks { get; init; }
    public double ClickThroughRate { get; init; }
    public double Current7DayClickThroughRate { get; init; }
    public double Previous7DayClickThroughRate { get; init; }
    public double ClickThroughRateDelta7Day { get; init; }
}

public sealed class ContentStudioDraftSummary
{
    public Guid DraftId { get; init; }
    public string Topic { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RevisionNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class ContentStudioWorkflowEntry
{
    public string EventType { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string PerformedBy { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed class ArticleHeroImagePreview
{
    public static ArticleHeroImagePreview Empty { get; } = new();

    public string Prompt { get; init; } = string.Empty;
    public string AltText { get; init; } = string.Empty;
    public string DataUri { get; init; } = string.Empty;
    public string ThemeLabel { get; init; } = string.Empty;
    public string AccentLabel { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ConfiguredModel { get; init; } = string.Empty;
    public string AvailableModelsSummary { get; init; } = string.Empty;
    public string StatusMessage { get; init; } = string.Empty;
    public bool IsGeneratedByOllama { get; init; }
}
