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

    public static readonly string[] All = [Note, Deal, Invoice, Ticket, Booking, Form, Email, Portal];

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
        _ => kind
    };
}

// One thing that happened with a contact. Link is an admin page to open it in, when there is one.
public sealed record ContactTimelineEntry(DateTimeOffset At, string Kind, string Title, string? Detail, string? Link);

public interface IContactTimelineService
{
    // Everything recorded about the contact across CRM, billing, support, scheduling, forms,
    // email and the client portal, newest first.
    Task<IReadOnlyList<ContactTimelineEntry>> GetTimelineAsync(Guid contactId, CancellationToken cancellationToken = default);
}
