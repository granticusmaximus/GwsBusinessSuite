using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GwsBusinessSuite.Infrastructure.Services;

// A contact's whole history in one list. Every module keeps its own rows keyed by ContactId, so
// this only reads - one query per module, all scoped to the one contact - and merges them.
// Sorting happens in memory: SQLite can't ORDER BY a DateTimeOffset column, and one contact's
// history is small.
public sealed class ContactTimelineService(IAppDbContextFactory dbContextFactory) : IContactTimelineService
{
    // Individual ticket messages and campaign emails can run long; the newest of each kind is
    // what's useful in a history, and the module pages hold the rest.
    private const int MaxTicketMessages = 100;
    private const int MaxEmailSends = 100;
    private static readonly CultureInfo UsCurrencyCulture = CultureInfo.GetCultureInfo("en-US");

    public async Task<IReadOnlyList<ContactTimelineEntry>> GetTimelineAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken);
        if (contact is null) return [];

        var entries = new List<ContactTimelineEntry>
        {
            new(contact.CreatedAt, ContactTimelineKinds.Note, "Contact created", Who(contact.CreatedBy), null)
        };
        if (contact.UnsubscribedFromCampaignsAt is { } unsubscribed)
        {
            entries.Add(new(unsubscribed, ContactTimelineKinds.Email, "Unsubscribed from all campaigns", null, null));
        }

        foreach (var note in await db.ContactActivities.AsNoTracking().Where(a => a.ContactId == contactId).ToListAsync(cancellationToken))
        {
            entries.Add(new(note.CreatedAt, ContactTimelineKinds.Note, "Note", Join(note.Note, Who(note.CreatedBy)), null));
        }

        foreach (var deal in await db.Deals.AsNoTracking().Where(d => d.ContactId == contactId).ToListAsync(cancellationToken))
        {
            entries.Add(new(deal.CreatedAt, ContactTimelineKinds.Deal, $"Deal opened: {deal.Title}", $"{deal.ValueUsd.ToString("C0", UsCurrencyCulture)} · {deal.Stage}", "/admin/crm"));
            if (deal.ClosedAt is { } closed)
            {
                entries.Add(new(closed, ContactTimelineKinds.Deal, $"Deal {(deal.Stage == DealStages.Won ? "won" : "lost")}: {deal.Title}", deal.ValueUsd.ToString("C0", UsCurrencyCulture), "/admin/crm"));
            }
        }

        foreach (var invoice in await db.Invoices.AsNoTracking().Include(i => i.LineItems).Where(i => i.ContactId == contactId).ToListAsync(cancellationToken))
        {
            var total = invoice.LineItems.Sum(l => l.Quantity * l.UnitPriceUsd).ToString("C2", UsCurrencyCulture);
            entries.Add(new(invoice.CreatedAt, ContactTimelineKinds.Invoice, $"Invoice drafted: {invoice.Title}", total, "/admin/billing"));
            if (invoice.SentAt is { } sent) entries.Add(new(sent, ContactTimelineKinds.Invoice, $"Invoice sent: {invoice.Title}", Join(total, invoice.DueDate is { } due ? $"due {due:MMM d, yyyy}" : null), "/admin/billing"));
            if (invoice.PaidAt is { } paid) entries.Add(new(paid, ContactTimelineKinds.Invoice, $"Invoice paid: {invoice.Title}", total, "/admin/billing"));
            if (invoice.Status == InvoiceStatuses.Void) entries.Add(new(invoice.UpdatedAt ?? invoice.CreatedAt, ContactTimelineKinds.Invoice, $"Invoice voided: {invoice.Title}", total, "/admin/billing"));
        }

        var tickets = await db.SupportTickets.AsNoTracking().Where(t => t.ContactId == contactId).ToListAsync(cancellationToken);
        var ticketSubjects = tickets.ToDictionary(t => t.Id, t => t.Subject);
        foreach (var ticket in tickets)
        {
            entries.Add(new(ticket.CreatedAt, ContactTimelineKinds.Ticket, $"Ticket opened: {ticket.Subject}", $"{ticket.Priority} priority", "/admin/support"));
            if (ticket.ResolvedAt is { } resolved) entries.Add(new(resolved, ContactTimelineKinds.Ticket, $"Ticket resolved: {ticket.Subject}", null, "/admin/support"));
            if (ticket.SatisfactionRating is { } rating)
            {
                entries.Add(new(ticket.UpdatedAt ?? ticket.CreatedAt, ContactTimelineKinds.Ticket, $"Rated support {rating}/5: {ticket.Subject}", ticket.SatisfactionComment, "/admin/support"));
            }
        }
        if (ticketSubjects.Count > 0)
        {
            var ticketIds = ticketSubjects.Keys.ToList();
            var messages = (await db.SupportTicketMessages.AsNoTracking().Where(m => ticketIds.Contains(m.TicketId)).ToListAsync(cancellationToken))
                .OrderByDescending(m => m.CreatedAt)
                .Take(MaxTicketMessages);
            foreach (var message in messages)
            {
                var fromContact = message.AuthorType == SupportTicketAuthorTypes.Contact;
                entries.Add(new(message.CreatedAt, ContactTimelineKinds.Ticket,
                    fromContact ? $"Wrote in: {ticketSubjects[message.TicketId]}" : $"{message.AuthorName} replied: {ticketSubjects[message.TicketId]}",
                    Excerpt(message.Body), "/admin/support"));
            }
        }

        var bookings = await db.Bookings.AsNoTracking().Where(b => b.ContactId == contactId).ToListAsync(cancellationToken);
        if (bookings.Count > 0)
        {
            var typeIds = bookings.Select(b => b.BookingTypeId).Distinct().ToList();
            var typeNames = await db.BookingTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, cancellationToken);
            foreach (var booking in bookings)
            {
                var what = typeNames.GetValueOrDefault(booking.BookingTypeId) ?? "Meeting";
                entries.Add(new(booking.CreatedAt, ContactTimelineKinds.Booking, $"Booked: {what}", $"for {booking.StartsAt.ToLocalTime():MMM d, h:mm tt}", "/admin/scheduling"));
                if (booking.CancelledAt is { } cancelled)
                {
                    entries.Add(new(cancelled, ContactTimelineKinds.Booking, $"Cancelled: {what}", null, "/admin/scheduling"));
                }
                else if (booking.Status == BookingStatuses.Confirmed)
                {
                    entries.Add(new(booking.StartsAt, ContactTimelineKinds.Booking, $"{what}", booking.StartsAt > DateTimeOffset.UtcNow ? "upcoming" : null, "/admin/scheduling"));
                }
            }
        }

        // A submission is this contact's when it was linked to them, or (for older ones, before
        // linking existed) when it carried their email address.
        var email = contact.Email?.Trim().ToLowerInvariant();
        var hasEmail = !string.IsNullOrEmpty(email);
        var forms = await db.FormSubmissions.AsNoTracking()
            .Where(f => f.ContactId == contactId || (hasEmail && f.ContactId == null && f.Email != null && f.Email.ToLower() == email))
            .ToListAsync(cancellationToken);
        if (forms.Count > 0)
        {
            var pageIds = forms.Select(f => f.PageId).Distinct().ToList();
            var pageTitles = await db.CmsPages.AsNoTracking().Where(p => pageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);
            foreach (var form in forms)
            {
                entries.Add(new(form.CreatedAt, ContactTimelineKinds.Form, $"Submitted a form: {pageTitles.GetValueOrDefault(form.PageId) ?? "a page"}", null, $"/admin/form-submissions/{form.Id}"));
            }
        }

        var enrollments = await db.EmailCampaignEnrollments.AsNoTracking().Where(e => e.ContactId == contactId).ToListAsync(cancellationToken);
        var subscriptions = await db.EmailCampaignSubscriptions.AsNoTracking().Where(s => s.ContactId == contactId).ToListAsync(cancellationToken);
        var campaignIds = enrollments.Select(e => e.CampaignId).Concat(subscriptions.Select(s => s.CampaignId)).Distinct().ToList();
        var campaignNames = campaignIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.EmailCampaigns.AsNoTracking().Where(c => campaignIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        foreach (var enrollment in enrollments)
        {
            var name = campaignNames.GetValueOrDefault(enrollment.CampaignId) ?? "a campaign";
            entries.Add(new(enrollment.CreatedAt, ContactTimelineKinds.Email, $"Enrolled in {name}", null, "/admin/email-campaigns"));
            if (enrollment.CompletedAt is { } done) entries.Add(new(done, ContactTimelineKinds.Email, $"Finished {name}", null, "/admin/email-campaigns"));
        }
        if (enrollments.Count > 0)
        {
            var enrollmentCampaign = enrollments.ToDictionary(e => e.Id, e => e.CampaignId);
            var enrollmentIds = enrollmentCampaign.Keys.ToList();
            var sends = (await db.EmailCampaignSendLogs.AsNoTracking().Where(s => enrollmentIds.Contains(s.EnrollmentId)).ToListAsync(cancellationToken))
                .OrderByDescending(s => s.CreatedAt)
                .Take(MaxEmailSends)
                .ToList();
            var stepIds = sends.Select(s => s.StepId).Distinct().ToList();
            var stepSubjects = await db.EmailCampaignSteps.AsNoTracking().Where(s => stepIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Subject, cancellationToken);
            foreach (var send in sends)
            {
                entries.Add(new(send.CreatedAt, ContactTimelineKinds.Email,
                    send.Succeeded ? $"Sent: {stepSubjects.GetValueOrDefault(send.StepId) ?? "campaign email"}" : $"Failed to send: {stepSubjects.GetValueOrDefault(send.StepId) ?? "campaign email"}",
                    Join(campaignNames.GetValueOrDefault(enrollmentCampaign[send.EnrollmentId]), send.Succeeded ? null : send.ErrorMessage), "/admin/email-campaigns"));
            }
        }
        foreach (var subscription in subscriptions)
        {
            var name = campaignNames.GetValueOrDefault(subscription.CampaignId) ?? "an alert list";
            entries.Add(new(subscription.CreatedAt, ContactTimelineKinds.Email, $"Signed up for {name}", subscription.SourcePath, "/admin/email-campaigns"));
            if (subscription.ConfirmedAt is { } confirmed) entries.Add(new(confirmed, ContactTimelineKinds.Email, $"Confirmed {name}", null, "/admin/email-campaigns"));
            if (subscription.UnsubscribedAt is { } left) entries.Add(new(left, ContactTimelineKinds.Email, $"Unsubscribed from {name}", null, "/admin/email-campaigns"));
        }

        foreach (var login in await db.ClientPortalLoginTokens.AsNoTracking().Where(t => t.ContactId == contactId && t.ConsumedAt != null).ToListAsync(cancellationToken))
        {
            entries.Add(new(login.ConsumedAt!.Value, ContactTimelineKinds.Portal, "Signed in to the client portal", null, null));
        }

        return entries.OrderByDescending(e => e.At).ToList();
    }

    private static string? Who(string? username) => string.IsNullOrWhiteSpace(username) ? null : $"by {username}";

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    private static string Excerpt(string body)
    {
        var flat = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }
}
