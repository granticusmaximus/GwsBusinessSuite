using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Operations;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Loads what CashFlowForecast needs (19-11-26), materialized first: SQLite can't compare DateTimeOffset.
public sealed class CashFlowService(IAppDbContextFactory contexts, TimeProvider time)
{
    public async Task<(CashFlowResult Result, IReadOnlyDictionary<Guid, string> Clients)> ForecastAsync(Guid? whatIfContact = null, int whatIfLateDays = 0,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.LineItems).AsSplitQuery().ToListAsync(ct);
        var deals = await db.Deals.AsNoTracking().ToListAsync(ct);
        var names = (await db.Contacts.AsNoTracking().Select(c => new { c.Id, c.FullName, c.Company }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => string.IsNullOrWhiteSpace(c.Company) ? c.FullName : $"{c.FullName} ({c.Company})");
        return (CashFlowForecast.Build(invoices, deals, names, time.GetUtcNow(), whatIfContact, whatIfLateDays), names);
    }
}
