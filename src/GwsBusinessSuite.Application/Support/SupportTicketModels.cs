using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Support;

// AdminBaseUrl is reused for both the admin inbox link (staff notification) and the client
// portal link (contact notification) - they're served from the same host in this app. Same
// hardcoded-default-with-config-override convention as FormNotificationOptions/
// GrowthReportEmailOptions.DashboardUrl.
public sealed class SupportNotificationOptions
{
    public const string SectionName = "SupportNotification";

    public string NotifyEmail { get; set; } = "grant@gwsapp.net";
    public string AdminBaseUrl { get; set; } = "https://admin.gwsapp.net";
}

public sealed record SupportTicketAttachmentView(
    Guid Id, string FileName, string ContentType, long SizeBytes);

// FileName/Content pairs handed in by the reply UI (both admin and client portal) - kept
// separate from SupportTicketAttachmentView so callers never need to round-trip raw bytes
// through the read model.
public sealed record SupportTicketAttachmentUpload(string FileName, byte[] Content);

public sealed record SupportTicketMessageView(
    Guid Id, string AuthorType, string AuthorName, string Body, DateTimeOffset CreatedAt,
    IReadOnlyList<SupportTicketAttachmentView> Attachments);

public sealed record SupportTicketView(
    Guid Id,
    Guid ContactId,
    string ContactName,
    string Subject,
    string Status,
    string Priority,
    string? AssignedToUsername,
    DateTimeOffset? LastRepliedAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<SupportTicketMessageView> Messages,
    DateTimeOffset CreatedAt,
    string TagsCsv,
    DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    int? SatisfactionRating,
    string? SatisfactionComment);

// Surfaced in the admin inbox and consumed by the one-shot SLA automation sweep. These remain
// reasonable, unconfigurable-for-now defaults; revisit as a real
// per-workspace setting if/when SLA enforcement becomes a real ask.
public static class SupportTicketSlaTargets
{
    public static (TimeSpan FirstResponse, TimeSpan Resolution) For(string priority) => priority switch
    {
        SupportTicketPriorities.Urgent => (TimeSpan.FromHours(1), TimeSpan.FromHours(4)),
        SupportTicketPriorities.High => (TimeSpan.FromHours(4), TimeSpan.FromHours(24)),
        SupportTicketPriorities.Low => (TimeSpan.FromHours(24), TimeSpan.FromHours(168)),
        _ => (TimeSpan.FromHours(8), TimeSpan.FromHours(72))
    };
}

public sealed record SupportTicketCannedResponseView(Guid Id, string Title, string Body);

public interface ISupportTicketService
{
    // statusFilter is one of SupportTicketStatuses, or null for every status.
    Task<IReadOnlyList<SupportTicketView>> ListTicketsAsync(string? statusFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportTicketView>> ListTicketsForContactAsync(Guid contactId, CancellationToken cancellationToken = default);
    Task<SupportTicketView?> GetTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);

    // authorType is SupportTicketAuthorTypes.Contact when raised from the client portal,
    // Staff when an admin opens one on a contact's behalf.
    Task<SupportTicketView> CreateTicketAsync(
        Guid contactId, string subject, string initialMessage, string authorType, string authorName,
        IReadOnlyList<SupportTicketAttachmentUpload>? attachments = null,
        CancellationToken cancellationToken = default);

    // A Contact reply to a Resolved/Closed ticket silently reopens it to Open - the contact
    // shouldn't have to know or care that the thread had been marked done on staff's side.
    Task<SupportTicketView> AddReplyAsync(
        Guid ticketId, string authorType, string authorName, string body,
        IReadOnlyList<SupportTicketAttachmentUpload>? attachments = null,
        CancellationToken cancellationToken = default);

    Task<SupportTicketView> SetStatusAsync(Guid ticketId, string status, string performedBy, CancellationToken cancellationToken = default);
    Task<SupportTicketView> SetPriorityAsync(Guid ticketId, string priority, string performedBy, CancellationToken cancellationToken = default);
    Task<SupportTicketView> AssignAsync(Guid ticketId, string? assignedToUsername, string performedBy, CancellationToken cancellationToken = default);

    // tagsCsv is stored verbatim (trimmed) - same "raw string, parsed at read time" convention
    // as AutomationWorkflow.TagsCsv.
    Task<SupportTicketView> SetTagsAsync(Guid ticketId, string tagsCsv, string performedBy, CancellationToken cancellationToken = default);

    // Contact-only, once per ticket - the client portal shows the prompt only while
    // SatisfactionRating is still null on an already-Resolved ticket, but this also
    // double-checks server-side rather than trusting that client-side gate alone.
    Task<SupportTicketView> SubmitSatisfactionRatingAsync(
        Guid ticketId, int rating, string? comment, CancellationToken cancellationToken = default);

    // Returns null when the attachment doesn't exist. Callers (the /support/attachments/{id}
    // endpoint) are responsible for their own access check before calling this - it does not
    // take a caller identity, since "admin" and "the owning contact" resolve differently.
    Task<(string FileName, string ContentType, byte[] Content)?> GetAttachmentContentAsync(
        Guid attachmentId, CancellationToken cancellationToken = default);

    // For the access check above: which contact (if any) owns the ticket an attachment
    // belongs to.
    Task<Guid?> GetAttachmentOwnerContactIdAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    // Detects newly overdue first-response/resolution targets, marks each breach once, and
    // dispatches the corresponding automation trigger. Returns breach events detected.
    Task<int> ProcessSlaBreachesAsync(CancellationToken cancellationToken = default);
}

// A standalone macro library staff insert into the reply composer verbatim - admin-only CRUD,
// not tied to any one ticket.
public interface ISupportTicketCannedResponseService
{
    Task<IReadOnlyList<SupportTicketCannedResponseView>> ListAsync(CancellationToken cancellationToken = default);
    Task<SupportTicketCannedResponseView> CreateAsync(string title, string body, string performedBy, CancellationToken cancellationToken = default);
    Task<SupportTicketCannedResponseView> UpdateAsync(Guid id, string title, string body, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
