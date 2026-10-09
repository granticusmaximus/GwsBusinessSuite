using System.Globalization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Application.TimeTracking;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class TimeTrackingService(IAppDbContext db, IBillingService billing, TimeProvider timeProvider) : ITimeTrackingService
{
    public const int MaxManualMinutes = 24 * 60;

    public async Task<TimeEntryView?> GetRunningAsync(string username, CancellationToken cancellationToken = default)
    {
        var running = await db.TimeEntries.AsNoTracking().FirstOrDefaultAsync(t => t.Username == username && t.EndedAt == null, cancellationToken);
        return running is null ? null : (await ToViewsAsync([running], cancellationToken))[0];
    }

    public async Task<TimeEntryView> StartAsync(string username, TimeEntryInput input, CancellationToken cancellationToken = default)
    {
        await ValidateLinksAsync(input, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await StopRunningAsync(username, now, cancellationToken);
        var entry = NewEntry(username, input, now);
        db.TimeEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToViewsAsync([entry], cancellationToken))[0];
    }

    public async Task<TimeEntryView?> StopAsync(string username, CancellationToken cancellationToken = default)
    {
        var stopped = await StopRunningAsync(username, timeProvider.GetUtcNow(), cancellationToken);
        if (stopped is null) return null;
        await db.SaveChangesAsync(cancellationToken);
        return (await ToViewsAsync([stopped], cancellationToken))[0];
    }

    public async Task<TimeEntryView> AddManualAsync(string username, TimeEntryInput input, CancellationToken cancellationToken = default)
    {
        if (input.Minutes is <= 0 or > MaxManualMinutes)
            throw new ArgumentException($"Enter between 1 minute and {MaxManualMinutes / 60} hours.");
        await ValidateLinksAsync(input, cancellationToken);
        var entry = NewEntry(username, input, input.StartedAt);
        entry.Minutes = input.Minutes;
        entry.EndedAt = input.StartedAt.AddMinutes(input.Minutes);
        db.TimeEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToViewsAsync([entry], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = await db.TimeEntries.FirstOrDefaultAsync(t => t.Id == entryId, cancellationToken);
        if (entry is null) return;
        if (entry.InvoiceId is not null)
            throw new InvalidOperationException("This time is already on an invoice - delete or void that invoice first.");
        db.TimeEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TimeEntryView>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string? username = null,
        CancellationToken cancellationToken = default)
    {
        var from = fromUtc.ToUnixTimeSeconds();
        var to = toUtc.ToUnixTimeSeconds();
        var query = db.TimeEntries.AsNoTracking().Where(t => t.StartedAtUnixSeconds >= from && t.StartedAtUnixSeconds < to);
        if (!string.IsNullOrWhiteSpace(username)) query = query.Where(t => t.Username == username);
        var entries = await query.OrderBy(t => t.StartedAtUnixSeconds).ToListAsync(cancellationToken);
        return await ToViewsAsync(entries, cancellationToken);
    }

    public async Task<IReadOnlyList<TimeEntryView>> ListForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var entries = await db.TimeEntries.AsNoTracking().Where(t => t.ContactId == contactId)
            .OrderByDescending(t => t.StartedAtUnixSeconds).ToListAsync(cancellationToken);
        return await ToViewsAsync(entries, cancellationToken);
    }

    public async Task<IReadOnlyList<UnbilledTimeSummary>> ListUnbilledAsync(CancellationToken cancellationToken = default)
    {
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(t => t.Billable && t.InvoiceId == null && t.EndedAt != null)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0) return [];
        var contactIds = entries.Select(e => e.ContactId).Distinct().ToList();
        var names = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, cancellationToken);
        return entries.GroupBy(e => e.ContactId)
            .Select(g => new UnbilledTimeSummary(g.Key, names.GetValueOrDefault(g.Key) ?? "(deleted contact)", g.Count(), g.Sum(e => e.Minutes),
                g.Sum(e => TimeTrackingMath.Amount(e.Minutes, e.HourlyRateUsd))))
            .OrderByDescending(s => s.AmountUsd)
            .ToList();
    }

    public async Task<decimal> GetLastRateAsync(string username, CancellationToken cancellationToken = default) =>
        await db.TimeEntries.AsNoTracking().Where(t => t.Username == username && t.HourlyRateUsd > 0)
            .OrderByDescending(t => t.StartedAtUnixSeconds).Select(t => (decimal?)t.HourlyRateUsd).FirstOrDefaultAsync(cancellationToken) ?? 0m;

    public async Task<InvoiceView> BillUnbilledAsync(Guid contactId, string actor, CancellationToken cancellationToken = default)
    {
        // One transaction: the draft and the "billed" marks land together, or neither does - a
        // draft without the marks would let the same hours be billed twice.
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var entries = await db.TimeEntries
            .Where(t => t.ContactId == contactId && t.Billable && t.InvoiceId == null && t.EndedAt != null)
            .OrderBy(t => t.StartedAtUnixSeconds)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0) throw new InvalidOperationException("This contact has no unbilled time.");
        var views = await ToViewsAsync(entries, cancellationToken);

        var first = entries.Min(e => e.StartedAt).ToLocalTime();
        var last = entries.Max(e => e.StartedAt).ToLocalTime();
        var invoice = await billing.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contactId,
            Title = first.Date == last.Date
                ? $"Time for {first.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}"
                : $"Time for {first.ToString("MMM d", CultureInfo.InvariantCulture)} - {last.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}",
            LineItems = views.Select(v => new InvoiceLineItemEditorModel
            {
                Description = LineDescription(v),
                Quantity = 1,
                UnitPriceUsd = v.AmountUsd
            }).ToList()
        }, actor, cancellationToken);

        foreach (var entry in entries) entry.InvoiceId = invoice.Id;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoice;
    }

    // "Oct 9 · Support: Login trouble · Fixed SSO config · 1.5 h @ $120/h" - the hours and rate
    // live in the description because an invoice line's quantity is a whole number.
    public static string LineDescription(TimeEntryView entry)
    {
        var parts = new List<string> { entry.StartedAt.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture) };
        if (entry.TicketSubject is { } ticket) parts.Add($"Support: {ticket}");
        else if (entry.DealTitle is { } deal) parts.Add(deal);
        if (!string.IsNullOrWhiteSpace(entry.Description)) parts.Add(entry.Description.Trim());
        parts.Add($"{entry.Hours.ToString("0.##", CultureInfo.InvariantCulture)} h @ {entry.HourlyRateUsd.ToString("C0", CultureInfo.GetCultureInfo("en-US"))}/h");
        return string.Join(" · ", parts);
    }

    private TimeEntry NewEntry(string username, TimeEntryInput input, DateTimeOffset startedAt) => new()
    {
        Username = username,
        ContactId = input.ContactId,
        DealId = input.DealId,
        TicketId = input.TicketId,
        Description = Truncate((input.Description ?? string.Empty).Trim(), 500),
        StartedAt = startedAt,
        StartedAtUnixSeconds = startedAt.ToUnixTimeSeconds(),
        Billable = input.Billable,
        HourlyRateUsd = Math.Max(0, input.HourlyRateUsd),
        CreatedBy = username,
        CreatedAt = timeProvider.GetUtcNow()
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private async Task<TimeEntry?> StopRunningAsync(string username, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var running = await db.TimeEntries.FirstOrDefaultAsync(t => t.Username == username && t.EndedAt == null, cancellationToken);
        if (running is null) return null;
        running.EndedAt = now;
        running.Minutes = TimeTrackingMath.ElapsedMinutes(running.StartedAt, now);
        running.UpdatedAt = now;
        running.UpdatedBy = username;
        return running;
    }

    // The deal or ticket, when given, has to belong to the same contact - otherwise its name would
    // land on the wrong client's invoice.
    private async Task ValidateLinksAsync(TimeEntryInput input, CancellationToken cancellationToken)
    {
        if (!await db.Contacts.AnyAsync(c => c.Id == input.ContactId && c.TrashedAt == null, cancellationToken))
            throw new ArgumentException("Choose a contact for this time.");
        if (input.DealId is { } dealId && !await db.Deals.AnyAsync(d => d.Id == dealId && d.ContactId == input.ContactId, cancellationToken))
            throw new ArgumentException("That deal belongs to a different contact.");
        if (input.TicketId is { } ticketId && !await db.SupportTickets.AnyAsync(t => t.Id == ticketId && t.ContactId == input.ContactId, cancellationToken))
            throw new ArgumentException("That ticket belongs to a different contact.");
    }

    private async Task<IReadOnlyList<TimeEntryView>> ToViewsAsync(IReadOnlyList<TimeEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return [];
        var contactIds = entries.Select(e => e.ContactId).Distinct().ToList();
        var dealIds = entries.Where(e => e.DealId != null).Select(e => e.DealId!.Value).Distinct().ToList();
        var ticketIds = entries.Where(e => e.TicketId != null).Select(e => e.TicketId!.Value).Distinct().ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, cancellationToken);
        var deals = dealIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Deals.AsNoTracking().Where(d => dealIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Title, cancellationToken);
        var tickets = ticketIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.SupportTickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Subject, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return entries.Select(e => new TimeEntryView(
            e.Id, e.Username, e.ContactId, contacts.GetValueOrDefault(e.ContactId) ?? "(deleted contact)",
            e.DealId, e.DealId is { } d ? deals.GetValueOrDefault(d) : null,
            e.TicketId, e.TicketId is { } t ? tickets.GetValueOrDefault(t) : null,
            e.Description, e.StartedAt, e.EndedAt,
            e.EndedAt is null ? Math.Max(0, (int)(now - e.StartedAt).TotalMinutes) : e.Minutes,
            e.Billable, e.HourlyRateUsd, e.InvoiceId)).ToList();
    }
}
