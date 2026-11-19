using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Operations;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Loads what LooseEnds needs (19-11-26). The records are materialized and filtered in memory:
// SQLite can't compare DateTimeOffset values.
public sealed class LooseEndsService(IAppDbContextFactory contexts, TimeProvider time)
{
    public async Task<IReadOnlyList<LooseEnd>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var contacts = await db.Contacts.AsNoTracking().ToListAsync(ct);
        var deals = await db.Deals.AsNoTracking().ToListAsync(ct);
        var activities = await db.ContactActivities.AsNoTracking().ToListAsync(ct);
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.LineItems).AsSplitQuery().ToListAsync(ct);
        var tickets = await db.SupportTickets.AsNoTracking().ToListAsync(ct);
        return LooseEnds.Build(contacts, deals, activities, invoices, tickets, time.GetUtcNow());
    }
}
