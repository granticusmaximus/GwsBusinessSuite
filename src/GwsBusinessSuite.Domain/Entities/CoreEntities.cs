using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

public static class AppRoles
{
    public const string Admin       = "Admin";
    public const string Author      = "Author";
    public const string Contributor = "Contributor";

    public static readonly string[] All = [Admin, Author, Contributor];
}

public sealed class AppUser : AuditableEntity
{
    public required string Username     { get; set; }
    public string PasswordHash          { get; set; } = string.Empty;
    public string Role                  { get; set; } = AppRoles.Author;
    public bool   IsActive              { get; set; } = true;
}

public static class SeoArticleDraftStatuses
{
    public const string Draft = "Draft";
    public const string PendingReview = "PendingReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class SeoArticleWorkflowEventTypes
{
    public const string Generated = "Generated";
    public const string Revised = "Revised";
    public const string ManuallyEdited = "ManuallyEdited";
    public const string Approved = "Approved";
    public const string PublishedToSite = "PublishedToSite";
    public const string Rejected = "Rejected";
    public const string HeroImageRegenerated = "HeroImageRegenerated";
}

public static class ArticleStatuses
{
    public const string Draft = "Draft";
    public const string PendingReview = "PendingReview";
    public const string Published = "Published";
}

public static class ArticleSource
{
    public const string OllamaGenerated = "OllamaGenerated";
    public const string Manual = "Manual";
}

public static class AffiliateInteractionEventTypes
{
    public const string Impression = "Impression";
    public const string Click = "Click";
}

public sealed class Contact : AuditableEntity
{
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string Status { get; set; } = "Lead";
}

public sealed class WikiPage : AuditableEntity
{
    public required string Title { get; set; }
    public required string Slug { get; set; }
    public string Markdown { get; set; } = string.Empty;
}

public static class CmsFontPairings
{
    public const string Elegant = "elegant";
    public const string Modern = "modern";
    public const string Classic = "classic";
}

public sealed class CmsSite : AuditableEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string Theme { get; set; } = "Default";
    public string CustomCss { get; set; } = string.Empty;
    public string NavMenuJson { get; set; } = "[]";

    // Global design tokens (Elementor-style "Global Colors/Fonts") applied site-wide via
    // PublicSiteHtmlRenderer.Layout — defaults match the hardcoded values public-site.css
    // already shipped with, so existing sites render identically until an admin changes them.
    public string AccentColorHex { get; set; } = "#f59e0b";
    public string FontPairingKey { get; set; } = CmsFontPairings.Elegant;
}

public static class CmsPageStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
}

public sealed class CmsPage : AuditableEntity
{
    public Guid SiteId { get; set; }
    public Guid? ParentPageId { get; set; }
    public required string Title { get; set; }
    public required string Slug { get; set; }
    public string BlocksJson { get; set; } = "[]";
    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public string OgImageUrl { get; set; } = string.Empty;
    public string CustomCss { get; set; } = string.Empty;
    public string Status { get; set; } = CmsPageStatuses.Draft;
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
}

public sealed class CmsPageRevision : AuditableEntity
{
    public Guid PageId { get; set; }
    public int RevisionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string BlocksJson { get; set; } = "[]";
    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public string OgImageUrl { get; set; } = string.Empty;
    public string CustomCss { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class MediaAsset : AuditableEntity
{
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required string DataUri { get; set; }
    public string AltText { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class FormSubmission : AuditableEntity
{
    public Guid PageId { get; set; }

    // JSON object of { fieldKey: submittedValue }, since the "form" widget lets an admin
    // define arbitrary fields per page — there's no fixed set of columns that covers
    // every form. Keyed by the field's key (the HTML input's "name") rather than its
    // label, since the submit endpoint only has the raw posted field names to work with.
    public string FieldsJson { get; set; } = "{}";

    public bool IsRead { get; set; }
}

public sealed class CjConnectorSettings : AuditableEntity
{
    // Singleton row — always upserted using WellKnownId.
    public static readonly Guid WellKnownId = new("c0c00000-0000-0000-0000-000000000001");

    public string DeveloperKey { get; set; } = string.Empty;
    public string PublisherId { get; set; } = string.Empty;
    public string WebsiteId { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = "https://commissions.api.cj.com/query";
    public int MaxResults { get; set; } = 100;
}

public sealed class AffiliateOffer : AuditableEntity
{
    public required string Network { get; set; }
    public required string AdvertiserId { get; set; }
    public required string AdvertiserName { get; set; }
    public required string LinkName { get; set; }
    public string? RelationshipStatus { get; set; }
    public string? Category { get; set; }
    public string? TrackingUrl { get; set; }
    public DateTimeOffset? PromotionEndsAt { get; set; }
}

public sealed class SeoArticleDraft : AuditableEntity
{
    public required string Topic { get; set; }
    public required string TargetAudience { get; set; }
    public string PrimaryKeyword { get; set; } = string.Empty;
    public string SecondaryKeywords { get; set; } = string.Empty;
    public string Status { get; set; } = SeoArticleDraftStatuses.Draft;
    public string Title { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string EstimatedReadingTime { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    // Comma-separated free-form tags (same convention as SecondaryKeywords / WatchedTopic.Keywords).
    public string Tags { get; set; } = string.Empty;
    public string OutlineMarkdown { get; set; } = string.Empty;
    public string ArticleMarkdown { get; set; } = string.Empty;
    public string SeoChecklistMarkdown { get; set; } = string.Empty;
    public string SourceNotesMarkdown { get; set; } = string.Empty;
    public string RequestedModifications { get; set; } = string.Empty;
    public string HeroImagePrompt { get; set; } = string.Empty;
    public string HeroImageAltText { get; set; } = string.Empty;
    public string HeroImageDataUri { get; set; } = string.Empty;
    public string HeroImageThemeLabel { get; set; } = string.Empty;
    public string HeroImageAccentLabel { get; set; } = string.Empty;
    public string HeroImageCaption { get; set; } = string.Empty;
    public string HeroImageProvider { get; set; } = string.Empty;
    public string HeroImageConfiguredModel { get; set; } = string.Empty;
    public string HeroImageAvailableModelsSummary { get; set; } = string.Empty;
    public string HeroImageStatusMessage { get; set; } = string.Empty;
    public bool IsHeroImageGeneratedByOllama { get; set; }
    public int RevisionNumber { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public ICollection<SeoArticleAffiliatePlacement> AffiliatePlacements { get; set; } = new List<SeoArticleAffiliatePlacement>();
    public ICollection<SeoArticleWorkflowEvent> WorkflowEvents { get; set; } = new List<SeoArticleWorkflowEvent>();
}

public sealed class SeoArticleAffiliatePlacement : AuditableEntity
{
    public Guid SeoArticleDraftId { get; set; }
    public string SlotToken { get; set; } = string.Empty;
    public string AdvertiserId { get; set; } = string.Empty;
    public string AdvertiserName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;
    public string CallToActionText { get; set; } = "Explore Offer";
    public int SortOrder { get; set; }
    public SeoArticleDraft? Draft { get; set; }
}

public sealed class SeoArticleAffiliateInteraction : AuditableEntity
{
    public Guid SeoArticleDraftId { get; set; }
    public string SlotToken { get; set; } = string.Empty;
    public string AdvertiserId { get; set; } = string.Empty;
    public string EventType { get; set; } = AffiliateInteractionEventTypes.Impression;
}

public sealed class SeoArticleWorkflowEvent : AuditableEntity
{
    public Guid SeoArticleDraftId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public SeoArticleDraft? Draft { get; set; }
}

public sealed class Article : AuditableEntity
{
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string? Topic { get; set; }
    public string BodyMarkdown { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public string PrimaryKeyword { get; set; } = string.Empty;
    public string SecondaryKeywords { get; set; } = string.Empty;
    public string Author { get; set; } = "Grant Watson";
    public string EstimatedReadingTime { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    // Comma-separated free-form tags (same convention as SecondaryKeywords / WatchedTopic.Keywords).
    public string Tags { get; set; } = string.Empty;
    public string? HeroImageUrl { get; set; }
    public string HeroImageAltText { get; set; } = string.Empty;
    public string HeroImageCaption { get; set; } = string.Empty;
    public string HeroImageDataUri { get; set; } = string.Empty;
    public string Status { get; set; } = ArticleStatuses.Draft;
    public string Source { get; set; } = ArticleSource.Manual;
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? SourceDraftId { get; set; }
    public ICollection<ArticleAffiliatePlacement> AffiliatePlacements { get; set; } = new List<ArticleAffiliatePlacement>();
}

// Flat blog taxonomy (no hierarchy) - distinct from ArticleAffiliatePlacement.Category,
// which is an unrelated free-text CJ affiliate-network category string.
public sealed class ArticleCategory : AuditableEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
}

public sealed class ArticleAffiliatePlacement : AuditableEntity
{
    public Guid ArticleId { get; set; }
    public string SlotToken { get; set; } = string.Empty;
    public string AdvertiserId { get; set; } = string.Empty;
    public string AdvertiserName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;
    public string CallToActionText { get; set; } = "Explore Offer";
    public int SortOrder { get; set; }
    public Article? Article { get; set; }
}

public sealed class WatchedTopic : AuditableEntity
{
    public required string Name { get; set; }
    // Comma-separated search terms matched against article title + description
    public string Keywords { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#6366f1";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastFetchedAt { get; set; }
}

public sealed class NewsItem : AuditableEntity
{
    // Null = "Today's Top News" (not tied to a specific topic)
    public Guid? TopicId { get; set; }
    public required string Title { get; set; }
    public required string Url { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string OllamaSummary { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}
