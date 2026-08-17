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

public sealed record SupportTicketMessageView(
    Guid Id, string AuthorType, string AuthorName, string Body, DateTimeOffset CreatedAt);

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
    DateTimeOffset CreatedAt);

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
        CancellationToken cancellationToken = default);

    // A Contact reply to a Resolved/Closed ticket silently reopens it to Open - the contact
    // shouldn't have to know or care that the thread had been marked done on staff's side.
    Task<SupportTicketView> AddReplyAsync(
        Guid ticketId, string authorType, string authorName, string body, CancellationToken cancellationToken = default);

    Task<SupportTicketView> SetStatusAsync(Guid ticketId, string status, string performedBy, CancellationToken cancellationToken = default);
    Task<SupportTicketView> SetPriorityAsync(Guid ticketId, string priority, string performedBy, CancellationToken cancellationToken = default);
    Task<SupportTicketView> AssignAsync(Guid ticketId, string? assignedToUsername, string performedBy, CancellationToken cancellationToken = default);
}
