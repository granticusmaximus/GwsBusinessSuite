using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Operations;

// The next 90 days of money expected in (19-11-26), week by week. Sent invoices land on their due
// date moved by how late that client usually pays (the median of their paid invoices); open deals
// count at a share of their value by stage, 30 days after their expected close (the usual invoice
// terms). Money out isn't modelled: the app keeps no expense or recurring-cost records.
public sealed record CashFlowItem(string Label, Guid ContactId, DateTimeOffset ExpectedAt, decimal Amount, string Basis);
public sealed record CashFlowWeek(DateTimeOffset Start, decimal FromInvoices, decimal FromDeals, IReadOnlyList<CashFlowItem> Items)
{
    public decimal Total => FromInvoices + FromDeals;
}
public sealed record CashFlowResult(IReadOnlyList<CashFlowWeek> Weeks, decimal FromInvoices, decimal FromDeals, int OverdueInvoices);

public static class CashFlowForecast
{
    public const int HorizonDays = 91;
    public static readonly TimeSpan DealPaymentTerms = TimeSpan.FromDays(30);

    public static decimal StageShare(string stage) => stage switch
    {
        DealStages.Lead => 0.10m,
        DealStages.Qualified => 0.25m,
        DealStages.ProposalSent => 0.50m,
        DealStages.Negotiation => 0.75m,
        _ => 0m,
    };

    public static decimal Total(Invoice invoice) => invoice.LineItems.Sum(item => item.Quantity * item.UnitPriceUsd);

    // A client's usual lateness in days (never early): the median over their paid invoices.
    public static IReadOnlyDictionary<Guid, int> UsualLateness(IEnumerable<Invoice> invoices) =>
        invoices.Where(i => i.PaidAt is not null && i.DueDate is not null)
            .GroupBy(i => i.ContactId)
            .ToDictionary(g => g.Key, g =>
            {
                var days = g.Select(i => Math.Max(0, (int)Math.Round((i.PaidAt!.Value - i.DueDate!.Value).TotalDays))).Order().ToList();
                return days[days.Count / 2];
            });

    // whatIfLateDays moves every invoice and deal of whatIfContact (or of everyone, when null) later.
    public static CashFlowResult Build(IReadOnlyList<Invoice> invoices, IReadOnlyList<Deal> deals, IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset now, Guid? whatIfContact = null, int whatIfLateDays = 0)
    {
        var lateness = UsualLateness(invoices);
        var start = now.Date;
        var end = start.AddDays(HorizonDays);
        int Extra(Guid contactId) => whatIfLateDays > 0 && (whatIfContact is null || whatIfContact == contactId) ? whatIfLateDays : 0;
        string Name(Guid id) => names.GetValueOrDefault(id) ?? "Unknown contact";
        var items = new List<(CashFlowItem Item, bool Invoice)>();
        var overdue = 0;

        foreach (var invoice in invoices.Where(i => i.Status == InvoiceStatuses.Sent && i.PaidAt is null))
        {
            var due = invoice.DueDate ?? invoice.SentAt?.AddDays(30) ?? now;
            var late = lateness.GetValueOrDefault(invoice.ContactId);
            var expected = due.AddDays(late + Extra(invoice.ContactId));
            if (due < now) overdue++;
            if (expected < now) expected = now;
            items.Add((new CashFlowItem($"{invoice.Title} · {Name(invoice.ContactId)}", invoice.ContactId, expected, Total(invoice),
                late > 0 ? $"due {due.ToGwsDate()}, this client usually pays {late} days late" : $"due {due.ToGwsDate()}"), true));
        }

        foreach (var deal in deals.Where(d => d.ClosedAt is null && StageShare(d.Stage) > 0 && d.ExpectedCloseDate is not null))
        {
            var expected = deal.ExpectedCloseDate!.Value.Add(DealPaymentTerms).AddDays(Extra(deal.ContactId));
            if (expected < now) expected = now;
            var share = StageShare(deal.Stage);
            items.Add((new CashFlowItem($"{deal.Title} · {Name(deal.ContactId)}", deal.ContactId, expected, deal.ValueUsd * share,
                $"{deal.Stage} deal of {GwsMoney.Usd(deal.ValueUsd)} counted at {GwsMoney.Percent(share)}, closing {deal.ExpectedCloseDate!.Value.ToGwsDate()}"), false));
        }

        var weeks = Enumerable.Range(0, HorizonDays / 7).Select(w =>
        {
            var weekStart = new DateTimeOffset(start.AddDays(w * 7), TimeSpan.Zero);
            var inWeek = items.Where(x => x.Item.ExpectedAt >= weekStart && x.Item.ExpectedAt < weekStart.AddDays(7)).ToList();
            return new CashFlowWeek(weekStart, inWeek.Where(x => x.Invoice).Sum(x => x.Item.Amount), inWeek.Where(x => !x.Invoice).Sum(x => x.Item.Amount),
                inWeek.Select(x => x.Item).OrderBy(i => i.ExpectedAt).ToList());
        }).ToList();
        return new CashFlowResult(weeks, weeks.Sum(w => w.FromInvoices), weeks.Sum(w => w.FromDeals), overdue);
    }
}
