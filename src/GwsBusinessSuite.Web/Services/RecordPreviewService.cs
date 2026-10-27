using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Components;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Web.Services;

public sealed record RecordPreviewFact(string Label, string Value);

public sealed record RecordPreviewAction(string Label, string Url);

// Tone is a StatusTone's CSS name, so the card draws the same pill as the page.
public sealed record RecordPreview(string Kind, string Title, string? Status, string Tone, IReadOnlyList<RecordPreviewFact> Facts, string Url, IReadOnlyList<RecordPreviewAction> Actions);

// Hover previews (tech-forward T2): a contact, ticket or invoice summarised for the card app.js
// shows over a RelationshipChip, a list row or a ⌘K result. A DbContext per call from the
// factory, since the page, its islands and these requests share no state.
public sealed class RecordPreviewService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time)
{
    public static readonly string[] Kinds = ["contact", "ticket", "invoice"];

    public async Task<RecordPreview?> GetAsync(string kind, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        return kind switch
        {
            "contact" => await ContactAsync(db, id, now, ct),
            "ticket" => await TicketAsync(db, id, now, ct),
            "invoice" => await InvoiceAsync(db, id, now, ct),
            _ => null
        };
    }

    private static async Task<RecordPreview?> ContactAsync(ApplicationDbContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().Where(item => item.Id == id && item.TrashedAt == null)
            .Select(item => new { item.FullName, item.Company, item.Email, item.Status, item.FollowUpDate }).FirstOrDefaultAsync(ct);
        if (contact is null) return null;
        var openTickets = await db.SupportTickets.AsNoTracking()
            .CountAsync(item => item.ContactId == id && (item.Status == SupportTicketStatuses.Open || item.Status == SupportTicketStatuses.Pending), ct);
        var unpaid = await db.Invoices.AsNoTracking().CountAsync(item => item.ContactId == id && item.Status == InvoiceStatuses.Sent, ct);
        var facts = new List<RecordPreviewFact>();
        if (!string.IsNullOrWhiteSpace(contact.Company)) facts.Add(new("Company", contact.Company));
        if (!string.IsNullOrWhiteSpace(contact.Email)) facts.Add(new("Email", contact.Email));
        if (contact.FollowUpDate is { } followUp)
            facts.Add(new("Follow-up", followUp <= now ? $"due {Day(followUp)}" : Day(followUp)));
        facts.Add(new("Open", $"{openTickets} ticket{(openTickets == 1 ? "" : "s")} · {unpaid} unpaid invoice{(unpaid == 1 ? "" : "s")}"));
        var url = $"/admin/crm/contacts/{id}";
        var actions = new List<RecordPreviewAction> { new("Open contact", url) };
        if (!string.IsNullOrWhiteSpace(contact.Email)) actions.Add(new("Email", $"mailto:{contact.Email}"));
        return new RecordPreview("contact", contact.FullName, contact.Status, StatusTones.CssName(StatusTones.ForContact(contact.Status)), facts, url, actions);
    }

    private static async Task<RecordPreview?> TicketAsync(ApplicationDbContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new { item.Subject, item.Status, item.Priority, item.ContactId, item.ResolutionDueAt, item.CreatedAt }).FirstOrDefaultAsync(ct);
        if (ticket is null) return null;
        var contactName = await db.Contacts.AsNoTracking().Where(item => item.Id == ticket.ContactId).Select(item => item.FullName).FirstOrDefaultAsync(ct);
        var facts = new List<RecordPreviewFact>
        {
            new("Contact", contactName ?? "Unknown contact"),
            new("Priority", ticket.Priority),
            new("Opened", Moment(ticket.CreatedAt))
        };
        if (ticket.ResolutionDueAt is { } due && ticket.Status is SupportTicketStatuses.Open or SupportTicketStatuses.Pending)
            facts.Add(new("Due", due < now ? $"overdue since {Moment(due)}" : Moment(due)));
        var url = $"/admin/support?ticket={id}";
        var actions = new List<RecordPreviewAction> { new("Open ticket", url), new("Contact", $"/admin/crm/contacts/{ticket.ContactId}") };
        return new RecordPreview("ticket", ticket.Subject, ticket.Status, StatusTones.CssName(StatusTones.ForTicket(ticket.Status)), facts, url, actions);
    }

    private static async Task<RecordPreview?> InvoiceAsync(ApplicationDbContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new { item.Title, item.Status, item.Currency, item.ContactId, item.DueDate, item.SentAt, item.PaidAt }).FirstOrDefaultAsync(ct);
        if (invoice is null) return null;
        var lines = await db.InvoiceLineItems.AsNoTracking().Where(item => item.InvoiceId == id)
            .Select(item => new { item.Quantity, item.UnitPriceUsd }).ToListAsync(ct);
        var contactName = await db.Contacts.AsNoTracking().Where(item => item.Id == invoice.ContactId).Select(item => item.FullName).FirstOrDefaultAsync(ct);
        var facts = new List<RecordPreviewFact>
        {
            new("Client", contactName ?? "Unknown contact"),
            new("Total", InvoiceCurrencies.Format(lines.Sum(item => item.Quantity * item.UnitPriceUsd), invoice.Currency))
        };
        if (invoice.DueDate is { } due)
            facts.Add(new("Due", invoice.Status == InvoiceStatuses.Sent && due < now ? $"overdue since {Day(due)}" : Day(due)));
        if (invoice.PaidAt is { } paid) facts.Add(new("Paid", Day(paid)));
        else if (invoice.SentAt is { } sent) facts.Add(new("Sent", Day(sent)));
        var url = $"/admin/billing?invoice={id}";
        var actions = new List<RecordPreviewAction> { new("Open invoice", url), new("Client", $"/admin/crm/contacts/{invoice.ContactId}") };
        return new RecordPreview("invoice", invoice.Title, invoice.Status, StatusTones.CssName(StatusTones.ForInvoice(invoice.Status)), facts, url, actions);
    }

    private static string Day(DateTimeOffset value) => value.ToLocalTime().ToString("dd-MM-yy");

    private static string Moment(DateTimeOffset value) => value.ToLocalTime().ToString("dd-MM-yy HH:mm");
}
