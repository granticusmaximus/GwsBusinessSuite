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
    public const string RevisionRestored = "RevisionRestored";
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
    public Guid? ParentWikiPageId { get; set; }
}

public static class CmsFontPairings
{
    public const string Elegant = "elegant";
    public const string Modern = "modern";
    public const string Classic = "classic";
}

public static class PublicationWindows
{
    public static bool IsVisible(string status, string publishedStatus, DateTimeOffset? publishedAt, DateTimeOffset now) =>
        string.Equals(status, publishedStatus, StringComparison.Ordinal)
        && publishedAt is { } publishAt
        && publishAt <= now;
}

public sealed class CmsSite : AuditableEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string Theme { get; set; } = "Default";
    public string CustomCss { get; set; } = string.Empty;

    // WordPress-style "theme locations": NavMenuJson is the Primary (header) menu — the
    // original single nav menu field, kept under its original name so existing sites'
    // menus survive untouched — and FooterNavMenuJson is the new Footer location. Two
    // flat, named locations (matching what a typical WordPress default theme registers)
    // rather than a generic n-location system, since that's all this site needs.
    public string NavMenuJson { get; set; } = "[]";
    public string FooterNavMenuJson { get; set; } = "[]";

    // Global design tokens (Elementor-style "Global Colors/Fonts") applied site-wide via
    // PublicSiteHtmlRenderer.Layout — defaults match the hardcoded values public-site.css
    // already shipped with, so existing sites render identically until an admin changes them.
    public string AccentColorHex { get; set; } = "#f59e0b";
    public string FontPairingKey { get; set; } = CmsFontPairings.Elegant;
    public string LogoUrl { get; set; } = string.Empty;
    public string FaviconUrl { get; set; } = string.Empty;
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
    public Guid? CategoryId { get; set; }
    public required string Title { get; set; }
    public required string Slug { get; set; }
    public string BlocksJson { get; set; } = "[]";
    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public string OgImageUrl { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string CustomCss { get; set; } = string.Empty;
    public string Status { get; set; } = CmsPageStatuses.Draft;
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
}

public sealed class CmsPageCategory : AuditableEntity
{
    public Guid SiteId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
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
    public string CanonicalUrl { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string CustomCss { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public static class GlobalBlockKinds
{
    public const string Widget = "Widget";
    public const string Section = "Section";
}

public sealed class GlobalBlock : AuditableEntity
{
    public Guid SiteId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = GlobalBlockKinds.Widget;
    public string? WidgetType { get; set; }
    public string Json { get; set; } = "{}";
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

public static class CommentStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Spam = "Spam";
}

public sealed class Comment : AuditableEntity
{
    public Guid ArticleId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string AuthorName { get; set; } = string.Empty;

    // Collected for the admin's own reference (spam-pattern review, potential future
    // notification/Gravatar) but never rendered on the public site.
    public string AuthorEmail { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = CommentStatuses.Pending;
}

public static class DockerHealthAlertSeverity
{
    public const string Warning = "Warning";
    public const string Error = "Error";
}

public sealed class DockerHealthAlert : AuditableEntity
{
    public string ContainerName { get; set; } = string.Empty;
    public string Severity { get; set; } = DockerHealthAlertSeverity.Error;

    // e.g. "Exited with code 137 (out of memory)" - the human-readable summary shown
    // in the notification bell and the alert history on the container's detail page.
    public string Message { get; set; } = string.Empty;

    public bool IsRead { get; set; }
}

public sealed class DockerActionLog : AuditableEntity
{
    // "droplet" for DigitalOcean-level actions (Reboot/Resize/Snapshot) that
    // aren't scoped to a single container.
    public string ContainerName { get; set; } = string.Empty;

    // Start/Stop/Restart/Remove/Pull/Recreate/Exec/Reboot/Resize/Snapshot/SshConnect/SshDisconnect
    public string Action { get; set; } = string.Empty;

    // Set only for Exec.
    public string? Command { get; set; }

    public bool Succeeded { get; set; }

    // Truncated output on success, error message on failure.
    public string? ResultSummary { get; set; }

    public string PerformedBy { get; set; } = string.Empty;
}

public sealed class DigitalOceanSettings : AuditableEntity
{
    // Singleton row — always upserted using WellKnownId.
    public static readonly Guid WellKnownId = new("d0000000-0000-0000-0000-000000000001");

    public string ApiToken { get; set; } = string.Empty;

    // Optional manual override; auto-detected from the droplet's local metadata
    // service (169.254.169.254) when blank and reachable.
    public string DropletId { get; set; } = string.Empty;

    // SSH terminal connection (private-key auth only). SshPrivateKey and
    // SshPrivateKeyPassphrase are protected via ISecretProtector, same as ApiToken.
    public string SshUsername { get; set; } = "root";
    public int SshPort { get; set; } = 22;
    public string SshPrivateKey { get; set; } = string.Empty;
    public string? SshPrivateKeyPassphrase { get; set; }

    // SHA256 fingerprint of the host key pinned on first successful connect. Not a
    // secret, so it's stored in plain text - encrypting it would make the "did the
    // host key change" check depend on decryption succeeding, when it should instead
    // fail safe (unreadable must never be treated as "no pinned key, allow anything").
    public string? SshHostKeyFingerprint { get; set; }
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

// WordPress-style "Settings" (General/Reading/Writing/Media/AI) — a singleton row for
// site-wide configuration that previously had no admin UI at all (see CmsSite for the
// site's Name/Slug/branding, which this deliberately does not duplicate).
public sealed class SiteSettings : AuditableEntity
{
    public static readonly Guid WellKnownId = new("51771145-0000-0000-0000-000000000001");

    public int PostsPerPage { get; set; } = 10;
    public Guid? DefaultArticleCategoryId { get; set; }
    public string? DefaultAuthorByline { get; set; }
    public string? OllamaModelOverride { get; set; }
    public int? OllamaTimeoutMinutesOverride { get; set; }
    public string? HeroImageModelOverride { get; set; }
    public int MaxMediaUploadSizeMb { get; set; } = 8;
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
    public ICollection<SeoArticleDraftRevision> Revisions { get; set; } = new List<SeoArticleDraftRevision>();
}

public sealed class SeoArticleDraftRevision : AuditableEntity
{
    public Guid SeoArticleDraftId { get; set; }
    public int VersionNumber { get; set; }
    public string ArticleMarkdown { get; set; } = string.Empty;
    public string OutlineMarkdown { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public SeoArticleDraft? Draft { get; set; }
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
    public DateTimeOffset? TrashedAt { get; set; }
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

public static class ArticleAffiliateSuggestionStatuses
{
    public const string Pending = "Pending";
    public const string Applied = "Applied";
    public const string Dismissed = "Dismissed";
}

// An AI-proposed (Ollama) pairing of an article with an AffiliateOffer, awaiting a one-click
// human Apply/Dismiss before it ever becomes a live ArticleAffiliatePlacement - see
// IAffiliateSuggestionService. Offer fields are snapshotted at generation time (not just an
// AffiliateOfferId FK) so a suggestion still displays sensibly even if the source offer is
// later resynced/removed from CJ.
public sealed class ArticleAffiliateSuggestion : AuditableEntity
{
    public Guid ArticleId { get; set; }
    public Guid AffiliateOfferId { get; set; }
    public string AdvertiserId { get; set; } = string.Empty;
    public string AdvertiserName { get; set; } = string.Empty;
    public string LinkName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Status { get; set; } = ArticleAffiliateSuggestionStatuses.Pending;
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

    // Only ever populated for sources that reliably provide one (e.g. dev.to's
    // cover_image) - Google News RSS items essentially never carry an image.
    public string? ImageUrl { get; set; }

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PodcastShow : AuditableEntity
{
    public required string Title { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string FeedUrl { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string AppleUrl { get; set; } = string.Empty;
    public string? ItunesId { get; set; }
    public DateTimeOffset? LastEpisodeRefreshAt { get; set; }
}

public sealed class PodcastEpisode : AuditableEntity
{
    public Guid PodcastShowId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AudioUrl { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string ExternalId { get; set; } = string.Empty;
}
