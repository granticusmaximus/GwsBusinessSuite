using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Operations;

// Things that slipped (19-11-26, "loose ends"): each with what it is, since when, and where to fix it.
public sealed record LooseEnd(string Kind, string Title, string Detail, DateTimeOffset Since, string Link);

public static class LooseEndKinds
{
    public const string FollowUp = "Follow-up overdue";
    public const string QuietDeal = "Deal gone quiet";
    public const string Invoice = "Invoice overdue";
    public const string Ticket = "Ticket unanswered";
    public static readonly IReadOnlyList<string> All = [FollowUp, Invoice, Ticket, QuietDeal];
}

public static class LooseEnds
{
    public static readonly TimeSpan QuietDealAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan TicketQuietAfter = TimeSpan.FromDays(3);

    // From records already loaded (the caller materializes them: SQLite can't compare
    // DateTimeOffset). Oldest first within each kind.
    public static IReadOnlyList<LooseEnd> Build(IReadOnlyList<Contact> contacts, IReadOnlyList<Deal> deals,
        IReadOnlyList<ContactActivity> activities, IReadOnlyList<Invoice> invoices, IReadOnlyList<SupportTicket> tickets, DateTimeOffset now)
    {
        var live = contacts.Where(c => c.TrashedAt is null).ToDictionary(c => c.Id);
        string Name(Guid contactId) => live.TryGetValue(contactId, out var c) ? (string.IsNullOrWhiteSpace(c.Company) ? c.FullName : $"{c.FullName} ({c.Company})") : "Unknown contact";
        string ContactLink(Guid contactId) => $"admin/crm/contacts/{contactId}";
        var lastActivity = activities.GroupBy(a => a.ContactId).ToDictionary(g => g.Key, g => g.Max(a => a.CreatedAt));
        var ends = new List<LooseEnd>();

        foreach (var contact in live.Values.Where(c => c.FollowUpDate is { } due && due < now))
            ends.Add(new(LooseEndKinds.FollowUp, Name(contact.Id), $"Follow-up was due {contact.FollowUpDate!.Value.ToGwsDate()}.", contact.FollowUpDate!.Value, ContactLink(contact.Id)));

        foreach (var invoice in invoices.Where(i => i.Status == InvoiceStatuses.Sent && i.PaidAt is null && i.DueDate is { } due && due < now))
        {
            var total = invoice.LineItems.Sum(item => item.Quantity * item.UnitPriceUsd);
            ends.Add(new(LooseEndKinds.Invoice, $"{invoice.Title} · {Name(invoice.ContactId)}",
                $"{GwsMoney.Usd(total)} due {invoice.DueDate!.Value.ToGwsDate()}, {(int)(now - invoice.DueDate!.Value).TotalDays} days ago" +
                (invoice.RemindersSent > 0 ? $"; {invoice.RemindersSent} reminder(s) sent." : "; no reminder sent."),
                invoice.DueDate.Value, "admin/billing"));
        }

        foreach (var ticket in tickets.Where(t => t.Status is SupportTicketStatuses.Open or SupportTicketStatuses.Pending))
        {
            var lastWord = ticket.LastRepliedAt ?? ticket.CreatedAt;
            var breached = ticket.LastRepliedAt is null && ticket.FirstResponseDueAt is { } due && due < now;
            if (!breached && now - lastWord < TicketQuietAfter) continue;
            ends.Add(new(LooseEndKinds.Ticket, $"{ticket.Subject} · {Name(ticket.ContactId)}",
                ticket.LastRepliedAt is null ? "No reply yet" + (breached ? "; first-response time passed." : ".") : $"No reply since {ticket.LastRepliedAt!.Value.ToGwsDate()}.",
                lastWord, "admin/support"));
        }

        foreach (var deal in deals.Where(d => d.ClosedAt is null && d.Stage is not (DealStages.Won or DealStages.Lost)))
        {
            var touched = new[] { deal.UpdatedAt ?? deal.CreatedAt, lastActivity.GetValueOrDefault(deal.ContactId) }.Max();
            if (now - touched < QuietDealAfter) continue;
            ends.Add(new(LooseEndKinds.QuietDeal, $"{deal.Title} · {Name(deal.ContactId)}",
                $"{deal.Stage}, {GwsMoney.Usd(deal.ValueUsd)}; nothing logged for {(int)(now - touched).TotalDays} days.", touched, ContactLink(deal.ContactId)));
        }

        return ends.OrderBy(e => LooseEndKindsOrder(e.Kind)).ThenBy(e => e.Since).ToList();
    }

    private static int LooseEndKindsOrder(string kind)
    {
        var index = LooseEndKinds.All.ToList().IndexOf(kind);
        return index < 0 ? int.MaxValue : index;
    }
}
