using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Campaigns;

// Everything an "ArticleAlerts" campaign lets the author configure, stored as JSON on
// EmailCampaign.ArticleAlertSettingsJson. The article card itself (title, publish date/time,
// hero image, excerpt, Read More) is generated, not authored - see ArticleAlertEmailRenderer.
public sealed class ArticleAlertSettings
{
    public const string DefaultMessage =
        "Hi {{subscriber.firstName}},\n\nI just published a new article I think you'll find useful:";

    public string FromName { get; set; } = "Grant Watson Software";
    public string FromAddress { get; set; } = "grant@gwsapp.net";
    public string ReplyTo { get; set; } = string.Empty;
    public string SubjectTemplate { get; set; } = "New post: {{article.title}}";

    // Markdown, written by the author, shown above the article card. Supports the tokens in
    // ArticleAlertTokens.
    public string Message { get; set; } = DefaultMessage;

    public bool ShowHeroImage { get; set; } = true;
    public int ExcerptLength { get; set; } = 180;
    public string ReadMoreLabel { get; set; } = "Read More";

    public string FooterLogoUrl { get; set; } = string.Empty;
    public string FooterBrandLine { get; set; } = "Grant Watson Software";
    public string FooterPhotoUrl { get; set; } = string.Empty;
    public string FooterSignOff { get; set; } = string.Empty;
    // CAN-SPAM requires a valid postal address in commercial email (a PO box is fine).
    public string MailingAddress { get; set; } = string.Empty;

    // Empty = every article. Otherwise an article matches when it's in one of the categories OR
    // carries one of the tags.
    public List<Guid> CategoryIds { get; set; } = [];
    public string TagFilter { get; set; } = string.Empty;

    // Alerts send this long after the article goes live, so a typo can be fixed or the post
    // unpublished first; an article that's no longer live by then is skipped.
    public int GraceMinutes { get; set; } = 15;
    public string TimeZoneId { get; set; } = "America/New_York";

    public string ConfirmationSubject { get; set; } = "Please confirm your subscription";
    public string ConfirmationMessage { get; set; } =
        "Hi {{subscriber.firstName}},\n\nThanks for signing up to hear about new articles. Please confirm it's really you:";
}

public static class ArticleAlertTokens
{
    public const string FirstName = "{{subscriber.firstName}}";
    public const string Title = "{{article.title}}";
    public const string PublishedAt = "{{article.publishedAt}}";
    public const string Url = "{{article.url}}";

    public static readonly IReadOnlyList<(string Token, string Meaning)> All =
    [
        (FirstName, "Subscriber's first name, or \"there\" when they didn't give one"),
        (Title, "The new article's title"),
        (PublishedAt, "When it was published (date and time)"),
        (Url, "Link to the article")
    ];
}

public sealed record ArticleAlertCampaignView(
    Guid Id,
    string Name,
    string Description,
    string Status,
    DateTimeOffset? ActivatedAt,
    ArticleAlertSettings Settings,
    int ConfirmedSubscriberCount,
    int PendingSubscriberCount);

public sealed record ArticleAlertSignupCampaignView(Guid Id, string Name, string Status, int ConfirmedSubscriberCount);

public sealed record ArticleAlertSubscriberView(
    Guid Id,
    Guid ContactId,
    string Email,
    string FirstName,
    string Status,
    DateTimeOffset SubscribedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? UnsubscribedAt,
    string SourcePath);

public sealed record ArticleAnnouncementView(
    Guid Id,
    Guid ArticleId,
    string ArticleTitle,
    string Status,
    DateTimeOffset DueAt,
    DateTimeOffset? CompletedAt,
    int RecipientCount,
    int DeliveredCount,
    int FailedCount);

public sealed class ArticleAlertSignupRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = string.Empty;
    [StringLength(80)]
    public string FirstName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string ConsentText { get; set; } = string.Empty;
}

public enum ArticleAlertSignupOutcome
{
    // Neutral on purpose: a new, pending or already-confirmed address all get the same "check your
    // inbox" answer, so the form can't be used to discover who is subscribed.
    CheckInbox,
    CampaignUnavailable,
    InvalidEmail
}

public enum ArticleAlertConfirmOutcome { Confirmed, AlreadyConfirmed, Expired, Invalid }

// A fully rendered email, transport-agnostic.
public sealed record OutgoingCampaignEmail(
    string FromName,
    string FromAddress,
    string ReplyTo,
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody,
    // Absolute https URL for RFC 8058 one-click unsubscribe (List-Unsubscribe + -Post headers);
    // null for mail that isn't a list send (e.g. the confirmation email).
    string? OneClickUnsubscribeUrl);

public interface IArticleAlertEmailSender
{
    // Throws on transport failure so the caller can record and retry; returns false when email
    // isn't configured at all (nothing to retry until it is).
    Task<bool> SendAsync(OutgoingCampaignEmail email, CancellationToken cancellationToken = default);
    bool IsConfigured { get; }
}

public interface IArticleAlertService
{
    Task<ArticleAlertCampaignView> CreateCampaignAsync(string name, string performedBy, CancellationToken cancellationToken = default);
    Task<ArticleAlertCampaignView?> GetCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default);
    Task<ArticleAlertCampaignView> SaveCampaignAsync(Guid campaignId, string name, string description, ArticleAlertSettings settings, string performedBy, CancellationToken cancellationToken = default);
    Task<ArticleAlertCampaignView> SetActiveAsync(Guid campaignId, bool active, string performedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArticleAlertSignupCampaignView>> ListSignupCampaignsAsync(CancellationToken cancellationToken = default);

    Task<ArticleAlertSignupOutcome> SubscribeAsync(Guid campaignId, ArticleAlertSignupRequest request, CancellationToken cancellationToken = default);
    Task<ArticleAlertConfirmOutcome> ConfirmAsync(string token, CancellationToken cancellationToken = default);
    Task<bool> UnsubscribeAsync(string token, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArticleAlertSubscriberView>> ListSubscribersAsync(Guid campaignId, CancellationToken cancellationToken = default);
    Task SetSubscriberStatusAsync(Guid subscriptionId, string status, string performedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArticleAnnouncementView>> ListAnnouncementsAsync(Guid campaignId, CancellationToken cancellationToken = default);
    Task RetryFailedAsync(Guid announcementId, CancellationToken cancellationToken = default);

    // Rendered with the latest live article (or a sample when there is none) - for the editor's
    // live preview and "Send test".
    Task<(string Subject, string Html)> RenderPreviewAsync(Guid campaignId, ArticleAlertSettings? unsavedSettings = null, CancellationToken cancellationToken = default);
    Task SendTestAsync(Guid campaignId, string toAddress, ArticleAlertSettings? unsavedSettings = null, CancellationToken cancellationToken = default);

    // The background sweep: schedules announcements for newly-live articles and sends the due ones.
    Task<int> ProcessAsync(CancellationToken cancellationToken = default);
}
