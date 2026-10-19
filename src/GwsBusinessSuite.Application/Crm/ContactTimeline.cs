namespace GwsBusinessSuite.Application.Crm;

// The kinds a contact's history can be filtered by.
public static class ContactTimelineKinds
{
    public const string Note = "note";
    public const string Deal = "deal";
    public const string Invoice = "invoice";
    public const string Ticket = "ticket";
    public const string Booking = "booking";
    public const string Form = "form";
    public const string Email = "email";
    public const string Portal = "portal";
    public const string Comment = "comment";
    public const string Time = "time";

    public static readonly string[] All = [Note, Deal, Invoice, Ticket, Booking, Time, Form, Email, Comment, Portal];

    public static string Label(string kind) => kind switch
    {
        Note => "Notes",
        Deal => "Deals",
        Invoice => "Invoices",
        Ticket => "Support",
        Booking => "Bookings",
        Form => "Forms",
        Email => "Email",
        Portal => "Portal",
        Comment => "Comments",
        Time => "Time",
        _ => kind
    };

    // Bootstrap Icons class shown beside an entry.
    public static string Icon(string kind) => kind switch
    {
        Deal => "bi-briefcase",
        Invoice => "bi-receipt",
        Ticket => "bi-life-preserver",
        Booking => "bi-calendar-event",
        Form => "bi-clipboard-data",
        Email => "bi-envelope",
        Portal => "bi-box-arrow-in-right",
        Comment => "bi-chat-left-text",
        Time => "bi-stopwatch",
        _ => "bi-journal-text"
    };
}

// Admin addresses that open one specific record, shared by the timeline and the pages that read them.
public static class ContactTimelineLinks
{
    public static string Ticket(Guid ticketId) => $"/admin/support?ticket={ticketId}";
    public static string Invoice(Guid invoiceId) => $"/admin/billing?invoice={invoiceId}";
}

// One thing that happened with a contact. Link is an admin page to open it in, when there is one.
public sealed record ContactTimelineEntry(DateTimeOffset At, string Kind, string Title, string? Detail, string? Link);

public interface IContactTimelineService
{
    // Everything recorded about the contact across CRM, billing, support, scheduling, time
    // tracking, forms, email, blog comments and the client portal, newest first.
    Task<IReadOnlyList<ContactTimelineEntry>> GetTimelineAsync(Guid contactId, CancellationToken cancellationToken = default);
}
