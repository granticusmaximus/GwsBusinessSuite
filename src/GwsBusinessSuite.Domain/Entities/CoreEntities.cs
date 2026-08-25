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

    // Reset to 0 on a successful login; incremented on each failed attempt while not
    // already locked out. Reaching Application.Users.LoginLockoutPolicy.MaxFailedAttempts
    // sets LockoutEndAt and resets this back to 0, so counting starts fresh after the
    // lockout expires (or is cleared early by an admin via UnlockUserAsync).
    public int FailedLoginAttempts      { get; set; }
    public DateTimeOffset? LockoutEndAt { get; set; }

    // Mandatory portal MFA. The TOTP seed is protected with ASP.NET Core Data
    // Protection; recovery codes are stored only as SHA-256 hashes. A nullable last
    // step prevents accepting the same authenticator code twice in one time window.
    public bool MfaEnabled { get; set; }
    public string MfaSecretProtected { get; set; } = string.Empty;
    public string MfaRecoveryCodeHashesJson { get; set; } = "[]";
    public long? MfaLastAcceptedStep { get; set; }
    public DateTimeOffset? MfaEnrolledAt { get; set; }
}

// Machine credential for the outbound developer API. Only a SHA-256 hash of the complete
// issued key is persisted; KeyPrefix is a non-secret selector/display hint used to find the
// candidate row without scanning every key. The plaintext key is returned exactly once.
public sealed class DeveloperApiKey : AuditableEntity
{
    public required string Name { get; set; }
    public required string KeyPrefix { get; set; }
    public required string KeyHash { get; set; }
    public required string ScopesCsv { get; set; }
    public int RateLimitPerMinute { get; set; } = 60;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public long RequestCount { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}

// One normalized, searchable representation of an application record. Embeddings remain in
// the primary SQLite database as compact float32 BLOBs so backups/restores are atomic with the
// source data. SourceType + SourceId is the durable identity; ParentId points a database-row
// document at its access-controlled parent database.
public sealed class SemanticSearchDocument : AuditableEntity
{
    public required string SourceType { get; set; }
    public Guid SourceId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public required string ContentHash { get; set; }
    public required string EmbeddingModel { get; set; }
    public int Dimensions { get; set; }
    public byte[] Embedding { get; set; } = [];
    public DateTimeOffset IndexedAt { get; set; }
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

public static class ContactStatuses
{
    public const string Lead = "Lead";
    public const string Prospect = "Prospect";
    public const string Customer = "Customer";
    public const string Inactive = "Inactive";

    public static readonly string[] All = [Lead, Prospect, Customer, Inactive];
}

public sealed class Contact : AuditableEntity
{
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string Status { get; set; } = ContactStatuses.Lead;
    public DateTimeOffset? FollowUpDate { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
    // Set the first time an invoice is sent to this contact (BillingService creates the
    // Stripe Customer lazily on first send, then reuses it for every later invoice) - null
    // for every contact until then, including contacts who never get billed.
    public string? StripeCustomerId { get; set; }
    // Null = subscribed. Set = suppressed from every campaign send, checked before each one -
    // a global opt-out, not per-campaign, matching how most small-team marketing tools treat
    // "unsubscribe" by default. The unsubscribe link itself needs no token storage at all -
    // EmailCampaignService encrypts this contact's Id directly via IDataProtectionProvider
    // (same mechanism as the OAuth connect-flow state elsewhere in this app) into a durable,
    // non-expiring link that decrypts back to the Id on click, so every email ever sent keeps
    // working rather than needing a stored, reusable token.
    public DateTimeOffset? UnsubscribedFromCampaignsAt { get; set; }
}

// Append-only note/activity log for a contact - CreatedAt/CreatedBy from AuditableEntity
// double as "when" and "who logged it"; entries are never edited or reordered.
public sealed class ContactActivity : AuditableEntity
{
    public Guid ContactId { get; set; }
    public required string Note { get; set; }
}

// Passwordless client-portal sign-in. A contact enters their email; if it matches a real
// Contact.Email, one of these is minted and emailed as a single-use link - no password is ever
// created, stored, or reset for client-portal accounts. ContactId is a loose reference (no
// FK/cascade), same convention as SentinelResourcePermission/SentinelPublicShare's own
// TargetId, since a trashed Contact shouldn't be blocked by a stray token row.
// Named "ClientPortal" (not "Portal") throughout this feature to stay unambiguous against the
// unrelated, pre-existing "PortalAccess" policy and AdminPortal namespace, which refer to the
// internal staff admin app shell (Home.razor's "/admin" dashboard) - a completely different
// concept from this external, contact-facing surface.
public sealed class ClientPortalLoginToken : AuditableEntity
{
    public Guid ContactId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? RequestedFromIp { get; set; }
}

public static class DealStages
{
    public const string Lead = "Lead";
    public const string Qualified = "Qualified";
    public const string ProposalSent = "ProposalSent";
    public const string Negotiation = "Negotiation";
    public const string Won = "Won";
    public const string Lost = "Lost";

    public static readonly string[] All = [Lead, Qualified, ProposalSent, Negotiation, Won, Lost];

    // Won/Lost are terminal - the pipeline board groups everything else as "open".
    public static readonly string[] Open = [Lead, Qualified, ProposalSent, Negotiation];
}

// A sales opportunity tied to a Contact - separate from ContactActivity (a free-text note
// log) because a deal needs its own lifecycle (stage, value, close date) that a contact can
// have several of over time (e.g. a repeat customer with one Won deal and one open Lead).
public sealed class Deal : AuditableEntity
{
    public Guid ContactId { get; set; }
    public required string Title { get; set; }
    public string Stage { get; set; } = DealStages.Lead;
    public decimal ValueUsd { get; set; }
    public DateTimeOffset? ExpectedCloseDate { get; set; }
    // Set when Stage first becomes Won or Lost - lets the pipeline distinguish "closed
    // yesterday" from "closed six months ago" without re-deriving it from UpdatedAt (which
    // also changes for unrelated edits, e.g. fixing a typo in Notes long after closing).
    public DateTimeOffset? ClosedAt { get; set; }
    public string Notes { get; set; } = string.Empty;
    // SQLite/EF Core can't translate a range comparison or ORDER BY against CreatedAt (a
    // DateTimeOffset column) - mirrors WebAnalyticsEvent.OccurredAtUnixSeconds/
    // CjCommissionRecord.CreatedAtUnixSeconds so BI dashboard queries can filter/sort/cap deals
    // in SQL by date range instead of loading the whole table.
    public long CreatedAtUnixSeconds { get; set; }
}

public static class InvoiceStatuses
{
    // A Draft only ever exists locally - nothing is created in Stripe until SendAsync.
    public const string Draft = "Draft";
    public const string Sent = "Sent";
    public const string Paid = "Paid";
    public const string Void = "Void";

    public static readonly string[] All = [Draft, Sent, Paid, Void];
}

// Billing header for a contact - line items are composed and edited locally while Draft, then
// SendInvoiceAsync creates the matching Stripe Customer/InvoiceItems/Invoice on first send and
// hands the customer a Stripe-hosted payment page (StripeHostedInvoiceUrl), so card data never
// touches this app. StripeInvoiceId stays null for a Draft that's never been sent.
public sealed class Invoice : AuditableEntity
{
    public Guid ContactId { get; set; }
    // Optional - an invoice doesn't have to trace back to a specific pipeline deal.
    public Guid? DealId { get; set; }
    public required string Title { get; set; }
    public string Status { get; set; } = InvoiceStatuses.Draft;
    public string Currency { get; set; } = "usd";
    public DateTimeOffset? DueDate { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string? StripeCustomerId { get; set; }
    public string? StripeInvoiceId { get; set; }
    public string? StripeHostedInvoiceUrl { get; set; }
    public ICollection<InvoiceLineItem> LineItems { get; set; } = new List<InvoiceLineItem>();
}

public sealed class InvoiceLineItem : AuditableEntity
{
    public Guid InvoiceId { get; set; }
    public required string Description { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitPriceUsd { get; set; }
    public Invoice? Invoice { get; set; }
}

public static class SupportTicketStatuses
{
    public const string Open = "Open";
    // Waiting on the contact's own reply, not staff's - distinct from Open so the admin
    // queue can be filtered down to "tickets actually needing a staff response".
    public const string Pending = "Pending";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";

    public static readonly string[] All = [Open, Pending, Resolved, Closed];
    // A ticket a contact replies to while in either of these gets silently reopened to
    // Open - see SupportTicketService.AddReplyAsync.
    public static readonly string[] Terminal = [Resolved, Closed];
}

public static class SupportTicketPriorities
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static readonly string[] All = [Low, Normal, High, Urgent];
}

// A contact-facing support thread - the client portal counterpart to the internal-only
// AutomationExecution/ContactActivity logs. Every reply (from either side) is a
// SupportTicketMessage; the ticket row itself only carries thread-level state.
public sealed class SupportTicket : AuditableEntity
{
    public Guid ContactId { get; set; }
    public required string Subject { get; set; }
    public string Status { get; set; } = SupportTicketStatuses.Open;
    public string Priority { get; set; } = SupportTicketPriorities.Normal;
    // Free-text staff username, not a foreign key to AppUser - mirrors AuditableEntity's own
    // CreatedBy/UpdatedBy convention rather than introducing a real FK relationship for what's
    // just a display/filter label. Null means unassigned.
    public string? AssignedToUsername { get; set; }
    public DateTimeOffset? LastRepliedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    // Comma-separated, same raw-string-field convention as AutomationWorkflow.TagsCsv /
    // AutomationWorkflowTemplate.TagsCsv - split with StringSplitOptions.RemoveEmptyEntries |
    // TrimEntries at read time, never normalized into a child table.
    public string TagsCsv { get; set; } = string.Empty;
    // Computed once from Priority at creation time (see SupportTicketService.SlaTargets) - not
    // recomputed if Priority later changes, since a target that moves after the clock starts
    // isn't a meaningful SLA. Null LastRepliedAt-relative "first response" is considered met the
    // moment any Staff message exists. Both are surfaced in the admin inbox and monitored by
    // the one-shot SLA automation sweep; they do not themselves enforce assignment or status.
    public DateTimeOffset? FirstResponseDueAt { get; set; }
    public DateTimeOffset? ResolutionDueAt { get; set; }
    // One-shot sweep markers. Persisted before dispatch so a restart or later sweep cannot
    // repeatedly fire the same breach automation.
    public DateTimeOffset? FirstResponseBreachNotifiedAt { get; set; }
    public DateTimeOffset? ResolutionBreachNotifiedAt { get; set; }
    // Set once by the contact via the client portal, shown once per ticket after it transitions
    // to Resolved (see SupportTicketService.SubmitSatisfactionRatingAsync's validation - not
    // status-gated at the DB level itself, since a later Closed ticket should still keep its
    // rating). 1-5, null means not yet rated.
    public int? SatisfactionRating { get; set; }
    public string? SatisfactionComment { get; set; }
    public ICollection<SupportTicketMessage> Messages { get; set; } = new List<SupportTicketMessage>();
}

public static class SupportTicketAuthorTypes
{
    public const string Contact = "Contact";
    public const string Staff = "Staff";
}

public sealed class SupportTicketMessage : AuditableEntity
{
    public Guid TicketId { get; set; }
    public string AuthorType { get; set; } = SupportTicketAuthorTypes.Staff;
    public required string AuthorName { get; set; }
    public required string Body { get; set; }
    public SupportTicket? Ticket { get; set; }
    public ICollection<SupportTicketAttachment> Attachments { get; set; } = new List<SupportTicketAttachment>();
}

// Stored the same base64-in-DB way as MediaAsset.DataUri, but deliberately has no image-only
// validation - a ticket attachment can be any file type. Unlike /media/{id}, which serves
// public CMS assets inline, attachment content is always served with a forced download
// Content-Disposition (see the /support/attachments/{id} endpoint) regardless of ContentType,
// so a malicious HTML/SVG upload from an untrusted contact can never render inline in a
// browser as stored XSS.
public sealed class SupportTicketAttachment : AuditableEntity
{
    public Guid MessageId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required string DataUri { get; set; }
    public long SizeBytes { get; set; }
    public SupportTicketMessage? Message { get; set; }
}

// A saved reply staff can insert into the composer verbatim - standalone, not tied to any one
// ticket (a macro library, not a per-ticket note).
public sealed class SupportTicketCannedResponse : AuditableEntity
{
    public required string Title { get; set; }
    public required string Body { get; set; }
}

// A bookable meeting type ("30-minute intro call") - anonymous visitors book against this at
// GET /book/{Slug}. AvailabilityJson holds a small list of recurring weekly windows
// (BookingAvailabilityWindow, serialized) rather than its own child-entity table, matching
// this codebase's existing convention for small structured blobs (NavMenuJson, BlocksJson)
// that are always read/written as a whole rather than queried row-by-row.
// Times in AvailabilityJson and every Booking.StartsAt/EndsAt are UTC - this app is
// single-tenant/self-hosted for one team, so there's deliberately no per-visitor timezone
// conversion; the admin defines availability in whatever "UTC" means for their own team.
public sealed class BookingType : AuditableEntity
{
    public required string Title { get; set; }
    public required string Slug { get; set; }
    public string Description { get; set; } = string.Empty;
    public int DurationMinutes { get; set; } = 30;
    // Gap enforced after each booked slot before the next one can start.
    public int BufferMinutes { get; set; } = 0;
    // Free-text staff username shown to the visitor and emailed on a new booking - same
    // "display label, not a real FK" convention as SupportTicket.AssignedToUsername.
    public string? OwnerUsername { get; set; }
    public bool IsActive { get; set; } = true;
    public string AvailabilityJson { get; set; } = "[]";
}

public sealed record BookingAvailabilityWindow(DayOfWeek DayOfWeek, TimeOnly Start, TimeOnly End);

public static class BookingStatuses
{
    public const string Confirmed = "Confirmed";
    public const string Cancelled = "Cancelled";
}

// A confirmed (or since-cancelled) slot against a BookingType. ManageTokenHash follows the
// same "mint a random token, store only its SHA-256 hash, hand the raw token to the visitor
// in a link" convention as ClientPortalLoginToken/SentinelPublicShare - the emailed
// "manage/cancel this booking" link is the only place the raw token ever exists outside this
// hash.
public sealed class Booking : AuditableEntity
{
    public Guid BookingTypeId { get; set; }
    // Loosely linked (no FK) - set when the attendee's email matches (or is used to create)
    // a CRM Contact, same convention as every other loose Guid reference in this codebase.
    public Guid? ContactId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public required string AttendeeName { get; set; }
    public required string AttendeeEmail { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string Status { get; set; } = BookingStatuses.Confirmed;
    public required string ManageTokenHash { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

public static class EmailCampaignStatuses
{
    // A Draft campaign never enrolls or sends anything - flipping to Active is what lets
    // EnrollContactAsync accept enrollments and the background sweep start sending them.
    public const string Draft = "Draft";
    public const string Active = "Active";
    // Paused freezes every enrollment's NextSendAt in place (the sweep simply skips a
    // paused campaign's due sends) rather than cancelling them, so resuming picks up
    // exactly where it left off.
    public const string Paused = "Paused";

    public static readonly string[] All = [Draft, Active, Paused];
}

// A drip/nurture sequence a Contact can be enrolled in - EmailCampaign is the header,
// EmailCampaignStep the ordered emails, EmailCampaignEnrollment tracks one contact's progress
// through it, and EmailCampaignSendLog is the audit trail of what was actually sent.
public sealed class EmailCampaign : AuditableEntity
{
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = EmailCampaignStatuses.Draft;
    public ICollection<EmailCampaignStep> Steps { get; set; } = new List<EmailCampaignStep>();
}

public sealed class EmailCampaignStep : AuditableEntity
{
    public Guid CampaignId { get; set; }
    public int StepOrder { get; set; }
    public required string Subject { get; set; }
    // Plain text with {{FirstName}}/{{FullName}} tokens - not the structured-block editor
    // WikiPage/CmsPage use, since a campaign step is a single short email, not a document.
    public string Body { get; set; } = string.Empty;
    // Days after the PREVIOUS step (or after enrollment, for step 1) before this step sends.
    public int DelayDays { get; set; }
    public EmailCampaign? Campaign { get; set; }
}

public static class EmailCampaignEnrollmentStatuses
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public sealed class EmailCampaignEnrollment : AuditableEntity
{
    public Guid CampaignId { get; set; }
    public Guid ContactId { get; set; }
    public string Status { get; set; } = EmailCampaignEnrollmentStatuses.Active;
    // Index into the campaign's ordered Steps of the NEXT step to send - not yet sent.
    public int NextStepIndex { get; set; }
    public DateTimeOffset? NextSendAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class EmailCampaignSendLog : AuditableEntity
{
    public Guid EnrollmentId { get; set; }
    public Guid StepId { get; set; }
    public bool Succeeded { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public static class SeoAuditContentTypes
{
    public const string Article = "Article";
    public const string CmsPage = "CmsPage";
}

// A point-in-time SEO/AI-search-readiness audit of one Article or CmsPage - loosely linked
// (no FK) to ContentId, same convention as every other loose Guid reference in this codebase,
// since an audited page can be deleted without needing its history deleted in lockstep.
// FindingsJson is the full deterministic checklist (see SeoAuditFinding); AiModel/AiSummary
// are null/empty when the AI-era readiness pass was skipped or unavailable, so a run always
// records at least the deterministic result even without a working Ollama connection.
public sealed class SeoAuditRun : AuditableEntity
{
    public required string ContentType { get; set; }
    public Guid ContentId { get; set; }
    public int Score { get; set; }
    public string FindingsJson { get; set; } = "[]";
    public string? AiModel { get; set; }
    public string AiSummary { get; set; } = string.Empty;
    public string AiSuggestionsJson { get; set; } = "[]";
}

// A translated copy of one Article or CmsPage, for one language. Loosely linked via
// ContentType+ContentId (same shape as SeoAuditRun) rather than an FK, so it works across two
// unrelated content tables without either of them knowing localization exists. Unlike
// SeoAuditRun this is a live, unique-per-(content,language) record rather than a run history -
// generating a new AI draft or saving an edit overwrites the existing row for that language.
public sealed class ContentLocalization : AuditableEntity
{
    public required string ContentType { get; set; }
    public Guid ContentId { get; set; }
    // BCP-47-ish code, e.g. "es", "fr", "pt-BR" - not validated against a fixed list.
    public required string LanguageCode { get; set; }
    public string Title { get; set; } = string.Empty;
    // Translated BodyMarkdown for an Article, or a translated copy of BlocksJson for a CmsPage.
    public string Body { get; set; } = string.Empty;
    public string? MetaDescription { get; set; }
    public string Status { get; set; } = ContentLocalizationStatuses.Draft;
    public bool IsAiGenerated { get; set; }
    public string? AiModel { get; set; }
}

public static class ContentLocalizationStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
}

public sealed class WikiPage : AuditableEntity
{
    public required string Title { get; set; }
    public required string Slug { get; set; }
    // Superseded by BlocksJson (see below) - kept only so the one-time startup backfill
    // (WikiMarkdownBackfillService) has something to read from. Safe to drop in a later
    // migration once every environment has actually run that backfill at least once.
    public string Markdown { get; set; } = string.Empty;
    public string BlocksJson { get; set; } = "[]";
    // Application-managed optimistic concurrency token for authored page content. SQLite
    // has no SQL Server-style rowversion, so every content writer increments this integer.
    public long ContentVersion { get; set; } = 1;
    public string? Icon { get; set; }
    public string? CoverImageUrl { get; set; }
    public int SortOrder { get; set; }
    public Guid? ParentWikiPageId { get; set; }
    // Null for pages authored directly in this app. Set on the page's first Notion sync and
    // used thereafter for upsert-by-external-id reconciliation - see NotionSyncService.
    public string? NotionId { get; set; }
    // Set when a synced page is archived/trashed/no longer returned by Notion; a soft flag,
    // not a delete, so nothing locally derived from the page (links, revisions) is lost.
    public DateTimeOffset? NotionArchivedAt { get; set; }
    // Remote edit watermark from the last successful content import. When Notion returns
    // the same last_edited_time on a later search, the expensive block/comment walk is skipped.
    public DateTimeOffset? NotionLastEditedAt { get; set; }
    // Stable identity parsed from a Notion Markdown/CSV/HTML workspace export. Kept separate
    // from NotionId so archive restores can reconcile records without being archived by the
    // live API connector when an export-only page is not visible to that integration.
    public string? NotionExportId { get; set; }
    // Local, user-initiated soft-delete - distinct from NotionArchivedAt above, which reflects
    // the *remote* Notion page's own archived state. A page can be trashed here without ever
    // having touched Notion at all.
    public DateTimeOffset? TrashedAt { get; set; }
    // Marks a page as owned/managed by an app feature rather than freely authored - e.g.
    // QuickNoteService's "quick-notes" root folder, found by this stable key rather than by
    // title (titles are user-editable and not unique). Null for every ordinary page. A second
    // system feature would pick its own distinct key; this is not an enum because the set of
    // system-managed pages is expected to grow one feature at a time, not be fixed up front.
    public string? SystemKey { get; set; }
    public ICollection<WikiPageRevision> Revisions { get; set; } = new List<WikiPageRevision>();
}

// Bounded DB-snapshot history, mirroring CmsPageRevision/PageRevisionService exactly
// (same MaxRevisionsPerPage trim-on-save pattern) - replaces the old git-commit-per-save
// model now that page content is structured blocks rather than a single Markdown string.
public sealed class WikiPageRevision : AuditableEntity
{
    public Guid WikiPageId { get; set; }
    public int RevisionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string BlocksJson { get; set; } = "[]";
    public string Label { get; set; } = string.Empty;
    public WikiPage? WikiPage { get; set; }
}

// Shared canonical content for Notion-style "synced blocks". A synced block instance is just
// an ordinary WikiBlock (Type = WikiBlockTypes.SyncedBlock) whose Props["sourceId"] points at
// one of these rows instead of carrying its own RichText - every instance re-hydrates from the
// row on read (WikiService.GetPageAsync) and every edit to any instance overwrites the row on
// save (WikiService.SavePageAsync), so instances never fork. Duplicating a block or a whole
// page preserves the Props (and therefore the shared sourceId), which is how a second instance
// normally comes into being.
public sealed class WikiSyncedBlockSource : AuditableEntity
{
    public string RichTextJson { get; set; } = "[]";
    public Guid? OriginWikiPageId { get; set; }
}

// Durable page-content snapshot used to create new Sentinel pages. Templates deliberately
// do not retain a foreign key to their source page so deleting or reorganizing that page
// cannot invalidate a reusable workspace template.
public sealed class SentinelPageTemplate : AuditableEntity
{
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string PageTitle { get; set; }
    public string BlocksJson { get; set; } = "[]";
    public string? Icon { get; set; }
    public string? CoverImageUrl { get; set; }
}

// Durable snapshot of a reusable group of Sentinel blocks. Materializing a template always
// assigns fresh block identities so discussions, revisions, and concurrent edits remain
// isolated from both the source page and every other insertion.
public sealed class SentinelBlockTemplate : AuditableEntity
{
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string BlocksJson { get; set; } = "[]";
}

// Durable, source-independent snapshot of an entire Sentinel database. The JSON contains
// its properties, rows (including page blocks), and views; materialization always remaps
// every internal identity so a template instance can evolve independently of its source.
public sealed class SentinelDatabaseTemplate : AuditableEntity
{
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string DatabaseTitle { get; set; }
    public string? Icon { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}

// Per-user workspace navigation state. TargetId is deliberately polymorphic (page or
// database), so there is no database FK; stale entries are pruned when the state is read.
public sealed class SentinelNavigationEntry : AuditableEntity
{
    public required string Username { get; set; }
    public Guid TargetId { get; set; }
    public bool IsDatabase { get; set; }
    public bool IsFavorite { get; set; }
    public DateTimeOffset LastOpenedAt { get; set; }
}

// A saved search targets a query string, not a page/database id, so it doesn't fit
// SentinelNavigationEntry's TargetId/IsDatabase shape above.
public sealed class SentinelSavedSearch : AuditableEntity
{
    public required string Username { get; set; }
    public required string Query { get; set; }
}

public sealed class SentinelDiscussion : AuditableEntity
{
    public Guid WikiPageId { get; set; }
    public Guid? BlockId { get; set; }
    public string? AnchorText { get; set; }
    public int? AnchorStart { get; set; }
    public int? AnchorEnd { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public WikiPage? WikiPage { get; set; }
    public ICollection<SentinelDiscussionComment> Comments { get; set; } = new List<SentinelDiscussionComment>();
}

public sealed class SentinelDiscussionComment : AuditableEntity
{
    public Guid SentinelDiscussionId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string Body { get; set; } = string.Empty;
    public SentinelDiscussion? Discussion { get; set; }
    public SentinelDiscussionComment? ParentComment { get; set; }
    public ICollection<SentinelDiscussionReaction> Reactions { get; set; } = new List<SentinelDiscussionReaction>();
    public string? NotionId { get; set; }
}

public sealed class SentinelDiscussionReaction : AuditableEntity
{
    public Guid SentinelDiscussionCommentId { get; set; }
    public required string Username { get; set; }
    public required string Emoji { get; set; }
    public SentinelDiscussionComment? Comment { get; set; }
}

public sealed class SentinelNotification : AuditableEntity
{
    public required string Username { get; set; }
    public required string Kind { get; set; }
    public Guid WikiPageId { get; set; }
    public Guid? SentinelDiscussionId { get; set; }
    public Guid? SentinelDiscussionCommentId { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public static class SentinelWorkspaceRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}

public static class SentinelAccessLevels
{
    public const string View = "view";
    public const string Comment = "comment";
    public const string Edit = "edit";
    public const string FullAccess = "fullAccess";
}

public sealed class SentinelWorkspaceMember : AuditableEntity
{
    public required string Username { get; set; }
    public string Role { get; set; } = SentinelWorkspaceRoles.Member;
}

public sealed class SentinelResourcePermission : AuditableEntity
{
    public Guid TargetId { get; set; }
    public bool IsDatabase { get; set; }
    public required string Username { get; set; }
    public string AccessLevel { get; set; } = SentinelAccessLevels.View;
}

public sealed class SentinelPublicShare : AuditableEntity
{
    public Guid TargetId { get; set; }
    public bool IsDatabase { get; set; }
    // A third target kind alongside page (both false) and database (IsDatabase=true) - a
    // read-only automation status/report page (Part 4.10), reusing this same token/password/
    // expiry/revoke infrastructure rather than building a parallel share mechanism.
    public bool IsAutomationWorkflow { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool AllowSearchIndexing { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    // Both null means no password gate. Salted (unlike TokenHash, which hashes an
    // already-high-entropy random token) because a share password is user-chosen and
    // low-entropy, so a bare SHA-256 would be rainbow-table-able.
    public string? PasswordSalt { get; set; }
    public string? PasswordHash { get; set; }
    // Lockout against brute-forcing a user-chosen password on this anonymous, unauthenticated
    // endpoint. FailedPasswordAttempts resets to 0 on a correct guess or once a lockout is
    // applied; PasswordLockedUntil blocks all further attempts (right or wrong) until it lapses.
    public int FailedPasswordAttempts { get; set; }
    public DateTimeOffset? PasswordLockedUntil { get; set; }
    // Incremented once per actual content view (after any password gate is cleared), not per
    // token resolution - so a wrong password guess or a bare metadata check never counts.
    public int ViewCount { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
}

public sealed class SentinelPresenceLease : AuditableEntity
{
    public Guid WikiPageId { get; set; }
    public required string Username { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    // SQLite/EF Core can't translate a server-side range filter on a DateTimeOffset column -
    // this shadow column lets ListAsync bound both its page-scoped query and its expired-lease
    // sweep to real, indexed SQL WHERE clauses instead of materializing every presence lease
    // across the entire workspace on every single poll of every page.
    public long LastSeenAtUnixSeconds { get; set; }
}

public static class SentinelAiRunStatuses
{
    public const string Completed = "completed";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    // A tool-calling-loop write proposal (see SentinelAiService.ProposeSetDatabaseRowPropertyAsync)
    // awaiting human confirmation - distinct from Approved/Rejected, which are the unrelated
    // "teach SentinelGPT from this answer" learning-memory review states on a completed chat
    // turn (SentinelAiPanel.razor's Approve/Reject buttons). Resolved via
    // ISentinelAiService.ResolvePendingToolActionAsync into either Completed (executed) or
    // Cancelled (declined) - never Approved/Rejected, to avoid colliding with that separate flow.
    public const string Pending = "pending";
    public const string Cancelled = "cancelled";
}

public sealed class SentinelAiRun : AuditableEntity
{
    public Guid ConversationId { get; set; }
    public Guid? WikiPageId { get; set; }
    public required string Action { get; set; }
    public string Instruction { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string Status { get; set; } = SentinelAiRunStatuses.Completed;
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    // JSON array of SentinelAiCitation (TargetId, IsDatabase, Title) - the workspace search
    // results actually folded into this run's grounding context, so a reviewer can see what
    // the answer is (and isn't) backed by. A lightweight JSON column rather than a child
    // table, same tier of complexity as AutomationNode.ParametersJson.
    public string CitationsJson { get; set; } = "[]";
    // Set only when Status == Pending: the exact tool call to execute if a human confirms it,
    // captured at proposal time so confirmation always executes precisely what was previewed
    // rather than re-deriving it from a later model turn. Null once resolved (Completed/Cancelled).
    public string? PendingToolName { get; set; }
    public string? PendingToolArgumentsJson { get; set; }
}

// Durable copy of a Notion-hosted file. Notion API file URLs are signed and expire, so
// imported page blocks point at this local record instead of retaining the temporary URL.
public sealed class SentinelImportedFile : AuditableEntity
{
    public required string NotionBlockId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Content { get; set; }
    public long SizeBytes { get; set; }
}

public static class WikiDatabasePropertyTypes
{
    // Exactly one Title property per database (required, primary label) - every other type
    // is repeatable.
    public const string Title = "title";
    public const string Text = "text";
    public const string Number = "number";
    public const string Select = "select";
    public const string MultiSelect = "multiSelect";
    public const string Date = "date";
    public const string Checkbox = "checkbox";
    public const string Url = "url";
    public const string Person = "person";
    public const string Files = "files";
    public const string Place = "place";
    public const string Formula = "formula";
    public const string Relation = "relation";
    public const string Rollup = "rollup";
    // Auto-populated, read-only, backed by the row's own CreatedAt - never stored in
    // PropertyValuesJson.
    public const string CreatedTime = "createdTime";
    // Same "auto-populated, read-only, never stored" shape as CreatedTime, backed by the
    // row's own UpdatedAt/UpdatedBy/CreatedBy instead.
    public const string LastEditedTime = "lastEditedTime";
    public const string LastEditedBy = "lastEditedBy";
    public const string CreatedBy = "createdBy";
    // Text-shaped storage (same as Text/Url), distinguished only for display (mailto:/tel:
    // links) - no separate validation is enforced server-side.
    public const string Email = "email";
    public const string Phone = "phone";
    // Same single-option storage as Select, but each WikiDatabasePropertyOption also carries
    // a Group (see WikiDatabaseStatusGroups) so Board views can group by group instead of by
    // raw option, matching Notion's Status property.
    public const string Status = "status";
    // Never stores a per-row value (like Formula/Rollup, it's excluded from SaveRowAsync's
    // incoming values) - its ConfigJson instead names an Automation workflow to run on click.
    // Wired to actually invoke that workflow in the Automation phase of this build-out.
    public const string Button = "button";
    // Auto-assigned once at row creation (never re-assigned on edit), scoped to the database:
    // stored as a plain Number value, optionally rendered with a prefix - see
    // WikiDatabasePropertyConfiguration.UniqueIdPrefix.
    public const string UniqueId = "uniqueId";
    // User-togglable "Verified"/"Not verified" stamp carrying who verified it and when -
    // see WikiVerificationState.
    public const string Verification = "verification";
    // Text-shaped storage (like Text/Email), but never client-writable - populated only by
    // WikiDatabaseService.GenerateAiFieldValueAsync, triggered explicitly by a "Generate"
    // click on the cell (never automatically on row save - an LLM call is slow and costs
    // real inference time, unlike Formula/Rollup's cheap synchronous re-materialization on
    // every read). ConfigJson carries AiPromptTemplate ("[Property Name]" references,
    // resolved the same way WikiDatabaseComputation's formula engine already resolves
    // bracketed property references) and AiModel (the Ollama model to call).
    public const string AiField = "aiField";
}

public static class WikiDatabaseStatusGroups
{
    public const string ToDo = "todo";
    public const string InProgress = "inProgress";
    public const string Complete = "complete";

    public static IReadOnlyList<string> All { get; } = [ToDo, InProgress, Complete];
}

public static class WikiDatabaseViewTypes
{
    public const string Table = "table";
    public const string Board = "board";
    public const string List = "list";
    public const string Gallery = "gallery";
    public const string Calendar = "calendar";
    public const string Timeline = "timeline";
    public const string Chart = "chart";
    public const string Form = "form";
    public const string Map = "map";
    public const string Feed = "feed";
    public const string Dashboard = "dashboard";
}

// Slots into the same sidebar tree as WikiPage (ParentWikiPageId). Page blocks may reference
// a database by id, but the canonical schema/rows stay here rather than being duplicated in
// block JSON; see docs/WIKI_NOTION_CLONE.md for the linked-vs-inline database distinction.
public sealed class WikiDatabase : AuditableEntity
{
    public required string Title { get; set; }
    public string? Icon { get; set; }
    public Guid? ParentWikiPageId { get; set; }
    public int SortOrder { get; set; }
    // See WikiPage.NotionId/NotionArchivedAt - same upsert-by-external-id + soft-archive
    // reconciliation, applied to databases instead of pages.
    public string? NotionId { get; set; }
    public DateTimeOffset? NotionArchivedAt { get; set; }
    public DateTimeOffset? NotionLastEditedAt { get; set; }
    public string? NotionExportId { get; set; }
    // Local, user-initiated soft-delete - see WikiPage.TrashedAt's own comment. Trashing a
    // database does not touch its rows; they're hidden transitively because the database
    // itself is excluded from normal loads, and reappear automatically on restore.
    public DateTimeOffset? TrashedAt { get; set; }
    // A structural lock mirrors Notion's database lock: rows remain editable and can be added,
    // while shared schema, view, metadata, and row-template mutations are rejected server-side.
    public bool IsLocked { get; set; }
    public ICollection<WikiDatabaseProperty> Properties { get; set; } = new List<WikiDatabaseProperty>();
    public ICollection<WikiDatabaseRow> Rows { get; set; } = new List<WikiDatabaseRow>();
    public ICollection<WikiDatabaseView> Views { get; set; } = new List<WikiDatabaseView>();
    public ICollection<WikiDatabaseRowTemplate> RowTemplates { get; set; } = new List<WikiDatabaseRowTemplate>();
}

public sealed class WikiDatabaseProperty : AuditableEntity
{
    public Guid WikiDatabaseId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public int SortOrder { get; set; }
    // Per-type configuration: Select/MultiSelect options, Formula expression, Relation
    // target/reciprocal-property identifiers, or Rollup relation/target/aggregation identifiers.
    public string ConfigJson { get; set; } = "{}";
    // See WikiPage.NotionId - lets NotionSyncService upsert this property by Notion's own
    // property id on re-sync instead of duplicating it.
    public string? NotionId { get; set; }
    public WikiDatabase? WikiDatabase { get; set; }
}

public sealed class WikiDatabaseRow : AuditableEntity
{
    public Guid WikiDatabaseId { get; set; }
    public int SortOrder { get; set; }
    // Dictionary<propertyId (string GUID), value> - value shape depends on the property's
    // Type: string for text/url/select, decimal for number, bool for checkbox, string[] of
    // option ids for multiSelect, ISO-8601 string for date. CreatedTime is never stored
    // here - it reads straight from CreatedAt.
    public string PropertyValuesJson { get; set; } = "{}";
    // Like every Notion database item, a Sentinel row is also a page with its own blocks.
    // PropertyValuesJson remains the view/schema data shown in tables and boards; BlocksJson
    // is the document body opened from any database view.
    public string BlocksJson { get; set; } = "[]";
    // Same as WikiPage.Icon/CoverImageUrl - a row is a page too, so it gets the same
    // presentation fields.
    public string? Icon { get; set; }
    public string? CoverImageUrl { get; set; }
    // See WikiPage.NotionId/NotionArchivedAt.
    public string? NotionId { get; set; }
    public DateTimeOffset? NotionArchivedAt { get; set; }
    public DateTimeOffset? NotionLastEditedAt { get; set; }
    public string? NotionExportId { get; set; }
    // Local, user-initiated soft-delete - see WikiPage.TrashedAt's own comment. Independent of
    // the parent WikiDatabase's own TrashedAt, for trashing a single row without trashing the
    // whole database.
    public DateTimeOffset? TrashedAt { get; set; }
    // Sub-items: a row nested under another row in the SAME database, distinct from Relation
    // (which links rows across two properties/databases). Null means top-level. The database
    // relationship uses ON DELETE SET NULL so deleting a parent promotes its children to roots.
    public Guid? ParentRowId { get; set; }
    public WikiDatabase? WikiDatabase { get; set; }
    public ICollection<WikiDatabaseRowRevision> Revisions { get; set; } = new List<WikiDatabaseRowRevision>();
}

// A reusable starting state for new rows in one database. Property IDs remain those of the
// owning database; materialization filters stale/system-managed keys before SaveRowAsync.
public sealed class WikiDatabaseRowTemplate : AuditableEntity
{
    public Guid WikiDatabaseId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string BlocksJson { get; set; } = "[]";
    public string DefaultPropertyValuesJson { get; set; } = "{}";
    public string? Icon { get; set; }
    public string? CoverImageUrl { get; set; }
    public WikiDatabase? WikiDatabase { get; set; }
}

// Bounded DB-snapshot history for a row's page body, mirroring WikiPageRevision exactly
// (same MaxRevisionsPerPage trim-on-save pattern - see WikiDatabaseService). Kept as its
// own table rather than reusing WikiPageRevision: that entity's WikiPageId FK and
// (WikiPageId, RevisionNumber) unique index are non-nullable and single-owner, so sharing
// it would mean relaxing both to support two different parent types.
public sealed class WikiDatabaseRowRevision : AuditableEntity
{
    public Guid WikiDatabaseRowId { get; set; }
    public int RevisionNumber { get; set; }
    public string BlocksJson { get; set; } = "[]";
    public string? Icon { get; set; }
    public string? CoverImageUrl { get; set; }
    public string Label { get; set; } = string.Empty;
    public WikiDatabaseRow? WikiDatabaseRow { get; set; }
}

public sealed class WikiDatabaseView : AuditableEntity
{
    public Guid WikiDatabaseId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public int SortOrder { get; set; }
    // {"filters":[{"propertyId","operator","value"}],"sorts":[{"propertyId","direction"}],
    //  "groupByPropertyId":"..."} (groupByPropertyId is board-only).
    public string ConfigJson { get; set; } = "{}";
    public string? NotionId { get; set; }
    public WikiDatabase? WikiDatabase { get; set; }
}

// Per-user filter/sort override layered on top of a shared WikiDatabaseView, mirroring how
// SentinelNavigationEntry overlays per-user state on a shared target without touching it.
// Only Filters/Sorts/FilterGroup from ConfigJson are read back out at merge time - every other
// view setting (grouping, page property order, etc.) always stays shared.
public sealed class WikiDatabaseViewPersonalization : AuditableEntity
{
    public Guid WikiDatabaseViewId { get; set; }
    public required string Username { get; set; }
    public string ConfigJson { get; set; } = "{}";
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

    // A fuller token set (named colors + a type scale + a spacing scale) than AccentColorHex/
    // FontPairingKey alone provide - see GwsBusinessSuite.Application.CmsBuilder.DesignTokenSet.
    // Additive: a widget's WidgetStyle only resolves against this when it references a token by
    // name, so every existing page with raw hex/size values renders identically either way.
    public string DesignTokensJson { get; set; } = "{}";

    // When set, every "form" widget submission anywhere on this site sends a notification email
    // here (see FormSubmissionService.SubmitAsync) with a link into the admin submission detail
    // page. Empty = no notification (the pre-existing, silent-save-only behavior), so this is
    // opt-in per site rather than forced on.
    public string FormNotificationEmail { get; set; } = string.Empty;
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
    // Part 6.1 (structured content fields) - Dictionary<propertyId(GUID string), value>,
    // same shape/helpers convention as WikiDatabaseRow.PropertyValuesJson/WikiPropertyValues,
    // keyed against this site's CmsPageProperty definitions.
    public string PropertyValuesJson { get; set; } = "{}";
    // Part 6.5 (scheduled publishing) - true only while this page's cms.pagePublishedTrigger
    // fire has been deferred because PublishedAt was in the future at save time (see
    // CmsBuilderService.SavePageAsync); CmsScheduledPublishBackgroundService clears it and fires
    // the trigger once PublishedAt actually arrives, so automations react at the real publish
    // moment instead of the moment someone scheduled it.
    public bool ScheduledPublishTriggerPending { get; set; }
    // Client-safe structural locking (Phase 2) - the page-level default a section/widget falls
    // back to when its own EditPermission is "Inherit". One of "Open"/"ContentOnly"/"Locked"
    // (see GwsBusinessSuite.Application.CmsBuilder.CmsEditPermissions - not referenced directly
    // since Domain doesn't depend on Application, same reason CmsPageStatuses below is defined
    // here rather than shared). Defaults to "Open" so existing pages are unaffected.
    public string EditPermission { get; set; } = "Open";
}

// Part 6.1: one row per typed custom field defined for a CmsSite (shared across every page on
// that site, same "define once, use per-row" relationship WikiDatabaseProperty has to
// WikiDatabase) - mirrors WikiDatabaseProperty's own shape exactly, narrowed to the field types
// a CMS page actually needs (see CmsPagePropertyTypes) rather than reusing the full
// Formula/Relation/Rollup/Person/etc. vocabulary built for Sentinel databases.
public sealed class CmsPageProperty : AuditableEntity
{
    public Guid SiteId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public int SortOrder { get; set; }
    // Only Select uses this today (a JSON array of option strings) - present for the same
    // forward-compatible reason WikiDatabaseProperty.ConfigJson exists.
    public string ConfigJson { get; set; } = "{}";
}

public static class CmsPagePropertyTypes
{
    public const string Text = "Text";
    public const string Number = "Number";
    public const string Date = "Date";
    public const string Select = "Select";
    public const string MediaReference = "MediaReference";

    public static readonly string[] All = [Text, Number, Date, Select, MediaReference];
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
    // Phase 3 (per-instance overrides) - a JSON string array of this block's own Props keys
    // that each placement may diverge on independently instead of staying 100% synced (e.g. a
    // shared "product box" widget where every placement shares layout/style but has its own
    // title/link). Widget-kind blocks only; empty for Section blocks. See
    // GwsBusinessSuite.Application.CmsBuilder.GlobalBlockOverridableFields.
    public string OverridableFieldsJson { get; set; } = "[]";
}

public sealed class MediaAsset : AuditableEntity
{
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required string DataUri { get; set; }
    public string AltText { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    // Null means "no thumbnail was generated" - either the original was already small
    // enough that a separate copy wouldn't help, or generation failed - and callers fall
    // back to serving DataUri directly (see MediaLibraryService.GetThumbnailContentAsync).
    // Always image/jpeg regardless of the original's format, to keep this small.
    public string? ThumbnailDataUri { get; set; }
}

public sealed class FormSubmission : AuditableEntity
{
    public Guid PageId { get; set; }

    // JSON object of { fieldLabel: submittedValue }, since the "form" widget lets an admin
    // define arbitrary fields per page — there's no fixed set of columns that covers every
    // form. Keyed by the field's configured display Label (resolved from the page's live
    // widget config at submit time - see Program.cs's ResolveFormFieldLabels), falling back
    // to the raw posted field key for any field the resolver can't find (e.g. the page's
    // form widget was edited/removed after this submission arrived).
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
    public bool AutomaticArticleRotationEnabled { get; set; } = true;
}

public sealed class NotionConnectorSettings : AuditableEntity
{
    // Singleton row — always upserted using WellKnownId.
    public static readonly Guid WellKnownId = new("00701104-0000-0000-0000-000000000001");

    // Internal-connection, PAT, or OAuth access token. Always encrypted at rest via
    // ISecretProtector and never returned to the browser.
    public string IntegrationToken { get; set; } = string.Empty;
    // OAuth rotates both access and refresh tokens together. Keeping this separate lets the
    // existing internal-token connector remain a supported fallback.
    public string OAuthRefreshToken { get; set; } = string.Empty;
    public string AuthenticationMode { get; set; } = "internal";
    public string? OAuthBotId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? WorkspaceIconUrl { get; set; }
    public DateTimeOffset? OAuthConnectedAt { get; set; }
    // Cosmetic - fetched once via GET /v1/users/me when the token is saved.
    public string? WorkspaceName { get; set; }
    public bool AutoSyncEnabled { get; set; } = true;
    public string SyncDirection { get; set; } = "import";
    public string SelectedNotionIdsJson { get; set; } = "[]";
    public bool AllowTwoWayWrites { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int LastSyncImportedCount { get; set; }
    public int LastSyncUpdatedCount { get; set; }
    public int LastSyncArchivedCount { get; set; }
    public int LastSyncDiscoveredCount { get; set; }
    public int LastSyncSkippedCount { get; set; }
    public int LastSyncEmptyContentCount { get; set; }
    public int LastSyncContentBlockCount { get; set; }
    // Connection-webhook verification token, encrypted through ISecretProtector. Notion
    // uses it as the HMAC-SHA256 key for X-Notion-Signature.
    public string WebhookVerificationToken { get; set; } = string.Empty;
    public DateTimeOffset? WebhookVerificationReceivedAt { get; set; }
    public DateTimeOffset? LastWebhookReceivedAt { get; set; }
    public string? LastWebhookEventType { get; set; }
}

// Durable webhook receipt ledger. Notion retries a delivery when it does not receive a
// successful response; the unique event id prevents the same signal from creating duplicate
// work while still acknowledging the retry.
public sealed class NotionWebhookEvent : AuditableEntity
{
    public required string NotionEventId { get; set; }
    public required string EventType { get; set; }
    public string? WorkspaceId { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public DateTimeOffset EventTimestamp { get; set; }
    public bool SyncQueued { get; set; }
}

public sealed class NotionSyncConflict : AuditableEntity
{
    public Guid WikiPageId { get; set; }
    public required string NotionId { get; set; }
    public required string FieldName { get; set; }
    public string LocalValueJson { get; set; } = "null";
    public string RemoteValueJson { get; set; } = "null";
    public DateTimeOffset RemoteEditedAt { get; set; }
    public string Status { get; set; } = "pending";
    public string? Resolution { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public WikiPage? WikiPage { get; set; }
}

// WordPress-style "Settings" (General/Reading/Writing/Media/AI) — a singleton row for
// site-wide configuration that previously had no admin UI at all (see CmsSite for the
// site's Name/Slug/branding, which this deliberately does not duplicate).
public sealed class SiteSettings : AuditableEntity
{
    public static readonly Guid WellKnownId = new("51771145-0000-0000-0000-000000000001");

    public int PostsPerPage { get; set; } = 12;
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
    public string? ImageUrl { get; set; }
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
    public string LinkName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
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
    public long? PublishedAtUnixSeconds { get; set; }
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
    public string LinkName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string CallToActionText { get; set; } = "Explore Offer";
    public int SortOrder { get; set; }
    public Article? Article { get; set; }
}

// One durable CJ assignment for an article's automatic sponsored card. Rows are kept as
// history so a reader who leaves an article open across a rotation boundary still follows
// the offer that was actually displayed. Current assignments are selected by their numeric
// UTC window columns; this keeps the hot public-blog query inside SQLite.
public sealed class ArticleAffiliateRotation : AuditableEntity
{
    public Guid ArticleId { get; set; }
    public Guid AffiliateOfferId { get; set; }
    public string AdvertiserId { get; set; } = string.Empty;
    public string AdvertiserName { get; set; } = string.Empty;
    public string LinkName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string CallToActionText { get; set; } = "Explore Offer";
    public DateTimeOffset StartsAt { get; set; }
    public long StartsAtUnixSeconds { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long ExpiresAtUnixSeconds { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? EndedAtUnixSeconds { get; set; }
    public Article? Article { get; set; }
}

// A real reader clicking a live article's ad card, recorded by the /go/{placementId}
// redirect endpoint before forwarding to TrackingUrl. Deliberately not FK'd to
// ArticleAffiliatePlacement (only to Article) so click history survives the article
// being re-edited/re-suggested and its placements replaced.
public sealed class ArticleAffiliateClick : AuditableEntity
{
    public Guid ArticleId { get; set; }
    public Guid PlacementId { get; set; }
    public string AdvertiserId { get; set; } = string.Empty;
    public string AdvertiserName { get; set; } = string.Empty;
    public string TrackingUrl { get; set; } = string.Empty;

    // SQLite/EF Core can't translate a DateTimeOffset range filter or ORDER BY server-side
    // (see AffiliateAnalyticsService's own comment on this) - this shadow column exists
    // specifically so the analytics dashboard's click query can bound itself with a real,
    // SQL-pushed-down Where+OrderByDescending+Take instead of loading every click row this
    // site has ever recorded into memory.
    public long CreatedAtUnixSeconds { get; set; }
}

public static class WebAnalyticsEventNames
{
    public const string PageView = "pageview";
    public const string Engagement = "engagement";
}

// Privacy-first, first-party website telemetry. VisitorKey is a random browser-generated
// identifier capped client-side at 90 days; SessionKey is scoped to the browser session.
// No cookie, fingerprint, raw IP address, full referrer URL, or full user-agent is persisted.
public sealed class WebAnalyticsEvent : AuditableEntity
{
    public required string EventName { get; set; }
    public required string VisitorKey { get; set; }
    public required string SessionKey { get; set; }
    public required string Path { get; set; }
    public string PageTitle { get; set; } = string.Empty;
    public string ReferrerHost { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Medium { get; set; } = string.Empty;
    public string Campaign { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "Unknown";
    public string BrowserFamily { get; set; } = "Unknown";
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public string RegionCode { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public int EngagementSeconds { get; set; }
    public long OccurredAtUnixSeconds { get; set; }
}

// A deliberately constrained, per-user BI tile. QueryShape, Metric, and Dimension are
// validated against the application-owned catalogue before execution; they are never SQL or
// arbitrary property names. This keeps self-serve reporting away from the live database query
// surface while still letting each staff user compose their own dashboard.
public sealed class BusinessIntelligenceWidget : AuditableEntity
{
    public required string OwnerUsername { get; set; }
    public required string Title { get; set; }
    public required string QueryShape { get; set; }
    public required string Metric { get; set; }
    public required string Dimension { get; set; }
    public string Visualization { get; set; } = "Bar";
    public int RangeDays { get; set; } = 30;
    public int SortOrder { get; set; }
}

// A saved mind-map document: one JSON-serialized node tree per row (Application-layer
// MindMapTreeJson owns the format - this entity stays a dumb "{}" default since Domain can't
// reference Application for a real fresh-root helper), the same "single blob for a tree within
// one document" shape WikiPage.BlocksJson uses, not a ParentId adjacency list - a mind map's
// nodes belong to one document, not to each other across separate rows. MindMapService.CreateAsync
// always overwrites TreeJson with a real fresh-root tree immediately, the same way
// BusinessIntelligenceService.SaveWidgetAsync never relies on entity-level defaults either.
// OwnerUsername is a real filter key (see MindMapService), not just an audit field - every
// list/get/save/delete call is scoped by it.
public sealed class MindMap : AuditableEntity
{
    public required string OwnerUsername { get; set; }
    public required string Title { get; set; }
    public string TreeJson { get; set; } = "{}";
    public int SortOrder { get; set; }
}

public static class AnalyticsGoalMatchTypes
{
    public const string Event = "Event";
    public const string PagePath = "PagePath";
    public static readonly string[] All = [Event, PagePath];
}

// A named conversion definition evaluated against the same minimized first-party event
// stream as the dashboard. PagePath patterns are exact unless they end in '*', which
// intentionally means prefix match (for example /checkout/success/*).
public sealed class AnalyticsGoal : AuditableEntity
{
    public required string Name { get; set; }
    public string MatchType { get; set; } = AnalyticsGoalMatchTypes.Event;
    public required string MatchValue { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AnalyticsFunnel : AuditableEntity
{
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<AnalyticsFunnelStep> Steps { get; set; } = new List<AnalyticsFunnelStep>();
}

public sealed class AnalyticsFunnelStep : AuditableEntity
{
    public Guid AnalyticsFunnelId { get; set; }
    public required string Name { get; set; }
    public string MatchType { get; set; } = AnalyticsGoalMatchTypes.PagePath;
    public required string MatchValue { get; set; }
    public int SortOrder { get; set; }
    public AnalyticsFunnel? AnalyticsFunnel { get; set; }
}

public static class AnalyticsSegmentDimensions
{
    public const string PagePath = "PagePath";
    public const string Event = "Event";
    public const string Source = "Source";
    public const string Medium = "Medium";
    public const string Campaign = "Campaign";
    public const string Referrer = "Referrer";
    public const string Device = "Device";
    public const string Browser = "Browser";
    public static readonly string[] All = [PagePath, Event, Source, Medium, Campaign, Referrer, Device, Browser];
}

public static class AnalyticsSegmentOperators
{
    public const string Is = "Equals";
    public const string Contains = "Contains";
    public const string StartsWith = "StartsWith";
    public static readonly string[] All = [Is, Contains, StartsWith];
}

public sealed class AnalyticsSegment : AuditableEntity
{
    public required string Name { get; set; }
    public ICollection<AnalyticsSegmentRule> Rules { get; set; } = new List<AnalyticsSegmentRule>();
}

public sealed class AnalyticsSegmentRule : AuditableEntity
{
    public Guid AnalyticsSegmentId { get; set; }
    public required string Dimension { get; set; }
    public required string Operator { get; set; }
    public required string Value { get; set; }
    public int SortOrder { get; set; }
    public AnalyticsSegment? AnalyticsSegment { get; set; }
}

// A dated business-context note overlaid on the analytics trend. The date is normalized to
// UTC midnight so SQLite can filter and index report windows without provider-specific date
// conversions. Multiple annotations may intentionally describe different events on one day.
public sealed class AnalyticsAnnotation : AuditableEntity
{
    public long OccurredOnUnixSeconds { get; set; }
    public required string Note { get; set; }
}

public static class AnalyticsReportFrequencies
{
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    public static readonly string[] All = [Weekly, Monthly];
}

public static class AnalyticsReportDeliveryStatuses
{
    public const string Never = "Never";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
}

// Durable delivery policy for one analytics email recipient. The indexed Unix timestamp
// keeps background due-work queries provider-safe on SQLite; delivery timestamps remain
// DateTimeOffset values because they are displayed but never used by a SQL ordering filter.
public sealed class AnalyticsReportSchedule : AuditableEntity
{
    public required string Name { get; set; }
    public required string RecipientEmail { get; set; }
    public string Frequency { get; set; } = AnalyticsReportFrequencies.Weekly;
    public int RangeDays { get; set; } = 7;
    public int DeliveryDay { get; set; } = 1;
    public int DeliveryHourUtc { get; set; } = 13;
    public bool IsActive { get; set; } = true;
    public long? NextRunAtUnixSeconds { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastDeliveredAt { get; set; }
    public string LastStatus { get; set; } = AnalyticsReportDeliveryStatuses.Never;
    public string LastError { get; set; } = string.Empty;
}

public static class SocialNetworks
{
    public const string Facebook = "Facebook";
    public const string X = "X";
    public const string LinkedIn = "LinkedIn";
    public static readonly string[] All = [Facebook, X, LinkedIn];
}

public static class SocialPostStatuses
{
    public const string Draft = "Draft";
    public const string Scheduled = "Scheduled";
    public const string Publishing = "Publishing";
    public const string Published = "Published";
    public const string PartiallyPublished = "PartiallyPublished";
    public const string Failed = "Failed";

    // A target-level status: this attempt failed but hasn't exhausted its retries yet, so the
    // owning post's overall Status stays Scheduled and the scheduler will pick it up again once
    // NextRetryAt elapses. Only after retries are exhausted does a target become permanently
    // Failed. There is no equivalent post-level status - a post with any RetryPending target
    // reports as Scheduled.
    public const string RetryPending = "RetryPending";
}

// One encrypted server-side credential per social identity/page. ExternalAccountId is a
// Facebook Page id, LinkedIn author URN, or X user id. ProtectedAccessToken is never
// returned to a Razor component.
public sealed class SocialAccount : AuditableEntity
{
    public required string Network { get; set; }
    public required string DisplayName { get; set; }
    public required string ExternalAccountId { get; set; }
    public string ProtectedAccessToken { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastPublishedAt { get; set; }
}

public sealed class SocialPost : AuditableEntity
{
    public required string Title { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string Status { get; set; } = SocialPostStatuses.Draft;
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public ICollection<SocialPostTarget> Targets { get; set; } = new List<SocialPostTarget>();
}

public sealed class SocialPostTarget : AuditableEntity
{
    public Guid SocialPostId { get; set; }
    public Guid SocialAccountId { get; set; }
    public required string Network { get; set; }
    public required string Content { get; set; }
    public string Status { get; set; } = SocialPostStatuses.Draft;
    public string ExternalPostId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public SocialPost? SocialPost { get; set; }
    public SocialAccount? SocialAccount { get; set; }
}

public static class SocialPostAlertSeverity
{
    public const string Warning = "Warning";
    public const string Error = "Error";
}

// Mirrors DockerHealthAlert's shape/read-tracking so NotificationBell can show both alert
// kinds through the same list/unread-count/mark-read surface. A dedicated table (rather than
// reusing DockerHealthAlert) because ContainerName isn't a meaningful field for a social post.
public sealed class SocialPostAlert : AuditableEntity
{
    public Guid SocialPostId { get; set; }
    public string PostTitle { get; set; } = string.Empty;
    public string Severity { get; set; } = SocialPostAlertSeverity.Error;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
}

// Best-effort import of CJ's own commission/transaction ledger for revenue reporting -
// see CjAffiliateService.FetchCommissionsAsync for the caveat that CJ's GraphQL
// commission-amount fields aren't independently verified against live docs here, so
// parsing is defensive and a schema mismatch just yields no rows rather than a crash.
public sealed class CjCommissionRecord : AuditableEntity
{
    // CJ's own commission/action id, used as the natural key for idempotent re-sync.
    public required string ExternalId { get; set; }
    public string AdvertiserId { get; set; } = string.Empty;
    public string AdvertiserName { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string ActionStatus { get; set; } = string.Empty;
    public decimal SaleAmount { get; set; }
    public decimal CommissionAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset? EventDate { get; set; }
    public DateTimeOffset? PostingDate { get; set; }

    // See ArticleAffiliateClick.CreatedAtUnixSeconds - same shadow-column reason, so the
    // analytics dashboard's commission query can bound itself at the SQL level too.
    public long CreatedAtUnixSeconds { get; set; }
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
    public string? ImageUrl { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Status { get; set; } = ArticleAffiliateSuggestionStatuses.Pending;
    public Article? Article { get; set; }
}

public static class WatchedTopicTypes
{
    // General: Google News RSS keyword search + dev.to - broad current-events coverage.
    // Technical: Hacker News Algolia search + dev.to, no Google News - keyword news search
    // is mostly noise for narrow programming terms (e.g. "Blazor", "C#"), so technical
    // topics get sources that actually carry developer discussion instead.
    public const string General = "General";
    public const string Technical = "Technical";

    public static readonly string[] All = [General, Technical];
}

public sealed class WatchedTopic : AuditableEntity
{
    public required string Name { get; set; }
    // Comma-separated search terms matched against article title + description
    public string Keywords { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#6366f1";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastFetchedAt { get; set; }
    public string TopicType { get; set; } = WatchedTopicTypes.Technical;
}

public sealed class NewsItem : AuditableEntity
{
    // Null = "All News"/"Breaking News" (the shared topicless pool, not tied to a specific topic)
    public Guid? TopicId { get; set; }
    public required string Title { get; set; }
    public required string Url { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    // Numeric UTC companions keep range filters and ordering inside SQLite. The original
    // DateTimeOffset values remain the display/source-of-truth fields.
    public long? PublishedAtUnixSeconds { get; set; }
    public string Description { get; set; } = string.Empty;
    public string OllamaSummary { get; set; } = string.Empty;

    // Only ever populated for sources that reliably provide one (e.g. dev.to's
    // cover_image) - Google News RSS items essentially never carry an image.
    public string? ImageUrl { get; set; }

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    public long FetchedAtUnixSeconds { get; set; }
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

// A reference source (e.g. "clean-room" notes on how WordPress/Elementor-style features
// behave) that CmsKnowledgeEntry rows are grouped under. Key is a human-readable slug
// kept from the original hardcoded seed data, not used as a foreign key (entries
// reference Id).
public sealed class CmsKnowledgeSource : AuditableEntity
{
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string LicenseNotes { get; set; } = string.Empty;
    public string UsageGuidance { get; set; } = string.Empty;
}

public sealed class CmsKnowledgeEntry : AuditableEntity
{
    public Guid SourceId { get; set; }
    public required string Capability { get; set; }
    public string WorkflowSummary { get; set; } = string.Empty;
    public string ImplementationHint { get; set; } = string.Empty;
    // Comma-separated (same convention as Article.Tags / WatchedTopic.Keywords).
    public string SuggestedBlocksCsv { get; set; } = string.Empty;
}

public static class AppGenerationRequestStatuses
{
    public const string Drafting = "Drafting";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class AppGenerationMessageRoles
{
    public const string User = "User";
    public const string Assistant = "Assistant";
}

// An Author-initiated chat session that iteratively refines what pages should be added
// to TargetSiteId. GeneratedPagesJson holds the latest agreed-upon List<GeneratedPageSpec>
// (see Application.AppGeneration) snapshotted at each assistant turn - submitting for
// approval just freezes whatever that snapshot was at the time. Nothing is written to
// CmsPages until an Admin approves (see IAppGenerationService.ApproveAsync).
public sealed class AppGenerationRequest : AuditableEntity
{
    public Guid TargetSiteId { get; set; }
    public required string Title { get; set; }
    public string Status { get; set; } = AppGenerationRequestStatuses.Drafting;
    public string GeneratedPagesJson { get; set; } = "[]";
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
    public ICollection<AppGenerationMessage> Messages { get; set; } = new List<AppGenerationMessage>();
}

// Append-only chat transcript row - CreatedAt/CreatedBy from AuditableEntity double as
// "when" and "who sent it" for User-role rows; Assistant-role rows use CreatedBy = "ollama".
public sealed class AppGenerationMessage : AuditableEntity
{
    public Guid AppGenerationRequestId { get; set; }
    public string Role { get; set; } = AppGenerationMessageRoles.User;
    public required string Content { get; set; }
    public AppGenerationRequest? Request { get; set; }
}

public static class LiveShowSessionStatuses
{
    public const string Live = "Live";
    public const string Ended = "Ended";
}

// A single broadcaster (Admin-only) going live to a handful of invited viewers over a
// direct WebRTC mesh (see LiveShowHub) - InviteToken is one shared link for every viewer
// of this session, not per-viewer, since the plan is a small trusted audience rather than
// public/anonymous discovery. Only one session is ever Live at a time in practice, but
// nothing here enforces that structurally - LiveShowService.StartSessionAsync ends any
// still-open prior session first.
public sealed class LiveShowSession : AuditableEntity
{
    public required string Title { get; set; }
    public string Status { get; set; } = LiveShowSessionStatuses.Live;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public required string InviteToken { get; set; }
    public DateTimeOffset InviteExpiresAt { get; set; }
}

// One MediaRecorder-captured file per session, written by the broadcaster's own browser
// tab as it streams (see liveShow.js) and uploaded in sequential chunks to
// /admin/api/live-show/{sessionId}/recording-chunk, then finalized once the show ends.
public sealed class LiveShowRecording : AuditableEntity
{
    public Guid SessionId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = "video/webm";
}

// Apple-Podcasts-style per-user resume position - keyed by Username (not an AppUser FK)
// to match this codebase's existing convention of storing "who" as a plain string
// (CreatedBy/UpdatedBy), since there's no public visitor account system, only the
// Admin/Author/Contributor accounts already tracked that way everywhere else. One row per
// (Username, EpisodeId); IsCompleted flips true once playback nears the end (see
// PodcastListenProgressService for the exact threshold) or the browser reports "ended".
public sealed class PodcastListenProgress : AuditableEntity
{
    public Guid EpisodeId { get; set; }
    public required string Username { get; set; }
    public int PositionSeconds { get; set; }
    public int? DurationSeconds { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset LastPlayedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AutomationWorkflowStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Inactive = "Inactive";
}

public static class AutomationExecutionStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
    public const string Waiting = "Waiting";
}

public static class AutomationExecutionModes
{
    public const string Manual = "Manual";
    public const string Webhook = "Webhook";
    public const string Schedule = "Schedule";
    public const string Retry = "Retry";
    public const string DatabaseTrigger = "DatabaseTrigger";
    public const string SubWorkflow = "SubWorkflow";
    public const string CrmDealStageChanged = "CrmDealStageChanged";
    public const string CmsPagePublished = "CmsPagePublished";
    public const string SentinelChatPromptSubmitted = "SentinelChatPromptSubmitted";
    public const string SupportTicketCreated = "SupportTicketCreated";
    public const string SupportTicketReplied = "SupportTicketReplied";
    public const string SupportTicketSlaBreached = "SupportTicketSlaBreached";
    // A sandboxed dry run of a past execution's recorded input against the current published
    // graph - see AutomationExecutionService.ReplayAsync. Never performs real side effects.
    public const string Replay = "Replay";
}

public sealed class AutomationWorkflow : AuditableEntity
{
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = AutomationWorkflowStatuses.Draft;
    public string TagsCsv { get; set; } = string.Empty;
    public int CurrentVersion { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? LastExecutedAt { get; set; }
    public string? WebhookPath { get; set; }
    public int? ScheduleIntervalMinutes { get; set; }
    // When set, takes precedence over ScheduleIntervalMinutes for computing NextScheduledAt -
    // see CronSchedule.GetNextOccurrence. Synced from an enabled core.scheduleTrigger node's
    // cronExpression parameter on Publish, same pattern as ScheduleIntervalMinutes itself.
    public string? ScheduleCronExpression { get; set; }
    public DateTimeOffset? NextScheduledAt { get; set; }
    public long? NextScheduledAtUnixSeconds { get; set; }
    // Synced from an enabled "database.rowChangedTrigger" node's ParametersJson on Publish,
    // same pattern as WebhookPath/ScheduleIntervalMinutes being synced from their own trigger
    // nodes - see AutomationWorkflowService.PublishAsync. Null means this workflow has no
    // (enabled) database trigger.
    public Guid? TriggerWikiDatabaseId { get; set; }
    // Opt-in escape hatch for the documented no-chaining tradeoff: database.setRowProperty/
    // database.addRow normally save as actor "automation-engine" specifically so
    // WikiDatabaseService.SaveRowAsync skips re-firing database.rowChangedTrigger (preventing
    // infinite automation loops). When this is true, those two node handlers use a different
    // actor string instead ("automation-engine:chained"), which WikiDatabaseService's actor
    // check doesn't special-case, so downstream automations DO see the write. Default false -
    // an author must deliberately opt in per workflow, understanding the loop risk they're
    // accepting.
    public bool AllowDownstreamAutomationTriggers { get; set; }
    // Synced on Publish from the presence of an enabled "crm.dealStageChangedTrigger" /
    // "cms.pagePublishedTrigger" node, same cached-subscriber-lookup pattern as
    // TriggerWikiDatabaseId above - lets AutomationTriggerService find subscribers without
    // deserializing every active workflow's published snapshot.
    public bool TriggerCrmDealStageChanged { get; set; }
    public bool TriggerCmsPagePublished { get; set; }
    // Same cached-subscriber-lookup pattern, synced from an enabled
    // "sentinel.chatPromptSubmittedTrigger" node - fires whenever a user sends a SentinelGPT
    // chat message (see SentinelGptGenerationCoordinator).
    public bool TriggerSentinelChatPromptSubmitted { get; set; }
    // Same cached-subscriber-lookup pattern, synced from an enabled
    // "support.ticketCreatedTrigger" / "support.ticketRepliedTrigger" node.
    public bool TriggerSupportTicketCreated { get; set; }
    public bool TriggerSupportTicketReplied { get; set; }
    public bool TriggerSupportTicketSlaBreached { get; set; }
    public ICollection<AutomationNode> Nodes { get; set; } = new List<AutomationNode>();
    public ICollection<AutomationConnection> Connections { get; set; } = new List<AutomationConnection>();
    public ICollection<AutomationWorkflowVersion> Versions { get; set; } = new List<AutomationWorkflowVersion>();
}

public sealed class AutomationNode : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public required string Name { get; set; }
    public required string TypeKey { get; set; }
    public int TypeVersion { get; set; } = 1;
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public Guid? CredentialId { get; set; }
    public bool IsDisabled { get; set; }
    public bool ContinueOnFail { get; set; }
    public bool RetryOnFail { get; set; }
    public int MaxTries { get; set; } = 1;
    public int WaitBetweenTriesMs { get; set; }
    public int TimeoutMs { get; set; }
    public string Notes { get; set; } = string.Empty;
    public AutomationWorkflow? Workflow { get; set; }
}

public sealed class AutomationConnection : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public Guid SourceNodeId { get; set; }
    public string SourceOutput { get; set; } = "main";
    public Guid TargetNodeId { get; set; }
    public string TargetInput { get; set; } = "main";
    public AutomationWorkflow? Workflow { get; set; }
}

public sealed class AutomationWorkflowVersion : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public int VersionNumber { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public string ChangeSummary { get; set; } = string.Empty;
    public AutomationWorkflow? Workflow { get; set; }
}

public sealed class AutomationCredential : AuditableEntity
{
    public required string Name { get; set; }
    public required string TypeKey { get; set; }
    public string ProtectedData { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class AutomationExecution : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public int WorkflowVersion { get; set; }
    public string Mode { get; set; } = AutomationExecutionModes.Manual;
    public string Status { get; set; } = AutomationExecutionStatuses.Queued;
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; set; }
    public long? StartedAtUnixSeconds { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? FinishedAtUnixSeconds { get; set; }
    public Guid? RetryOfExecutionId { get; set; }
    public string PendingStateJson { get; set; } = "{}";
    public long? HeartbeatAtUnixSeconds { get; set; }
    public Guid? WaitingNodeId { get; set; }
    public string? WaitingNodeName { get; set; }
    public string? WaitingNodeTypeKey { get; set; }
    public string? WaitingInputJson { get; set; }
    public DateTimeOffset? ResumeAt { get; set; }
    public long? ResumeAtUnixSeconds { get; set; }
    public string? ResumeToken { get; set; }
    public AutomationWorkflow? Workflow { get; set; }
    public ICollection<AutomationNodeExecution> NodeExecutions { get; set; } = new List<AutomationNodeExecution>();
}

public sealed class AutomationNodeExecution : AuditableEntity
{
    public Guid ExecutionId { get; set; }
    public Guid NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string NodeTypeKey { get; set; } = string.Empty;
    public string Status { get; set; } = AutomationExecutionStatuses.Queued;
    public int Attempt { get; set; } = 1;
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public long StartedAtUnixSeconds { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? FinishedAtUnixSeconds { get; set; }
    // True when this node's OutputJson was substituted from a prior execution's recorded output
    // rather than actually run - see AutomationExecutionService.ReplayAsync / IsDryRun. Only ever
    // set on a node with a real external side effect (HTTP, CRM/CMS writes, AI calls, etc.);
    // pure data/flow nodes always run for real even during a dry run.
    public bool IsSimulated { get; set; }
    public AutomationExecution? Execution { get; set; }
}

// A point-in-time copy of a workflow's editable draft (node positions/notes included, unlike
// the position-free publish snapshot in AutomationWorkflowVersion), captured via
// AutomationTemplateService.CreateFromWorkflowAsync. Instantiating a template always mints
// fresh node/connection identities (AutomationWorkflowService.CreateFromGraphAsync) so the new
// workflow is fully independent of both the source workflow and every other instantiation -
// same convention as SentinelDatabaseTemplate/SentinelPageTemplate.
public sealed class AutomationWorkflowTemplate : AuditableEntity
{
    public required string Name { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TagsCsv { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = "{}";
}

// Congressional Record floor-proceedings text for a single chamber/day, "saved for future
// reference" per the Civic Watch floor-status feature - official but same-day-delayed, not
// live-synced captioning. One row per (Chamber, SessionDate); FederalCivicFeedService
// upserts on its hourly cadence rather than inserting duplicates. SessionDate is stored at
// UTC midnight - per this app's SQLite/EF Core convention, DateTimeOffset range/order
// queries can't translate server-side, so any lookup materializes then filters client-side.
public sealed class CongressionalFloorTranscript : AuditableEntity
{
    public required string Chamber { get; set; }
    public DateTimeOffset SessionDate { get; set; }
    public required string SourceUrl { get; set; }
    public required string FullText { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Part 4.8 (mobile push approvals) - server-side readiness only. This table and the
// IPushNotificationSender abstraction it backs are the concrete contract a MAUI client can
// register against and poll today (GET /admin/api/mobile/approvals/pending), but no real push
// delivery is wired up: that needs APNs/FCM credentials and the separate MAUI client project,
// neither available/verified in this session. NoOpPushNotificationSender logs instead of
// sending, so that gap stays visible rather than silently pretending delivery happened.
public sealed class MobileDeviceRegistration : AuditableEntity
{
    public required string Username { get; set; }
    public required string Platform { get; set; }
    public required string PushToken { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}
