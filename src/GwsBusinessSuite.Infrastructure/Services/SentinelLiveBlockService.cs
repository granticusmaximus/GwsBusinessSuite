using System.Globalization;
using GwsBusinessSuite.Application.BusinessIntelligence;
using GwsBusinessSuite.Application.ContentCalendar;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Application.TimeTracking;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services;

// Live business blocks (Sentinel item 5, see SentinelLiveBlocks). Every card is read fresh from
// the area's own service; nothing is cached here, so the editor's periodic refresh shows changes
// made anywhere else. Callers must already have checked the viewer is an Admin.
public sealed class SentinelLiveBlockService(
    ICrmService crmService,
    ISupportTicketService ticketService,
    IBusinessIntelligenceService biService,
    IContentCalendarService calendarService,
    ITimeTrackingService timeTrackingService,
    TimeProvider timeProvider) : ISentinelLiveBlockService
{
    private const int MaxTargets = 12;
    private const int MaxBars = 8;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<IReadOnlyList<SentinelLiveTarget>> SearchAsync(string kind, string? query, string username, CancellationToken cancellationToken = default)
    {
        var term = (query ?? string.Empty).Trim();
        bool Matches(params string?[] values) =>
            term.Length == 0 || values.Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

        switch (kind)
        {
            case SentinelLiveBlockKinds.Contact:
                return (await crmService.ListContactsAsync(cancellationToken: cancellationToken))
                    .Where(contact => Matches(contact.FullName, contact.Email, contact.Company))
                    .OrderByDescending(contact => contact.UpdatedAt ?? contact.CreatedAt)
                    .Take(MaxTargets)
                    .Select(contact => new SentinelLiveTarget(contact.Id.ToString(), contact.FullName,
                        string.Join(" · ", new[] { contact.Company, contact.Email }.Where(value => !string.IsNullOrWhiteSpace(value)))))
                    .ToList();
            case SentinelLiveBlockKinds.Deal:
                return (await crmService.ListDealsAsync(cancellationToken))
                    .Where(deal => Matches(deal.Title, deal.ContactName))
                    .OrderByDescending(deal => deal.CreatedAt)
                    .Take(MaxTargets)
                    .Select(deal => new SentinelLiveTarget(deal.Id.ToString(), deal.Title, $"{deal.ContactName} · {StageName(deal.Stage)}"))
                    .ToList();
            case SentinelLiveBlockKinds.Ticket:
                return (await ticketService.ListTicketsAsync(cancellationToken: cancellationToken))
                    .Where(ticket => Matches(ticket.Subject, ticket.ContactName))
                    .OrderByDescending(ticket => ticket.LastRepliedAt ?? ticket.CreatedAt)
                    .Take(MaxTargets)
                    .Select(ticket => new SentinelLiveTarget(ticket.Id.ToString(), ticket.Subject, $"{ticket.ContactName} · {ticket.Status}"))
                    .ToList();
            case SentinelLiveBlockKinds.Chart:
                return (await biService.GetDashboardAsync(username, cancellationToken: cancellationToken))
                    .Where(widget => Matches(widget.Title, widget.Chart.MetricLabel))
                    .OrderBy(widget => widget.SortOrder)
                    .Take(MaxTargets)
                    .Select(widget => new SentinelLiveTarget(widget.Id.ToString(), widget.Title,
                        $"{widget.Chart.MetricLabel} by {widget.Chart.DimensionLabel} · last {widget.RangeDays} days"))
                    .ToList();
            default:
                return [];
        }
    }

    public async Task<SentinelLiveCard> GetCardAsync(string kind, string? recordId, string username, CancellationToken cancellationToken = default)
    {
        if (!SentinelLiveBlockKinds.All.Contains(kind))
            return SentinelLiveCard.Unavailable(kind, "This kind of live block isn't supported.");
        Guid id = Guid.Empty;
        if (SentinelLiveBlockKinds.NeedsRecord(kind) && !Guid.TryParse(recordId, out id))
            return SentinelLiveCard.Unavailable(kind, "Choose what to show first.");

        return kind switch
        {
            SentinelLiveBlockKinds.Contact => await ContactCardAsync(id, cancellationToken),
            SentinelLiveBlockKinds.Deal => await DealCardAsync(id, cancellationToken),
            SentinelLiveBlockKinds.Ticket => await TicketCardAsync(id, cancellationToken),
            SentinelLiveBlockKinds.Chart => await ChartCardAsync(id, username, cancellationToken),
            SentinelLiveBlockKinds.Calendar => await CalendarCardAsync(cancellationToken),
            _ => await TimeCardAsync(cancellationToken)
        };
    }

    private async Task<SentinelLiveCard> ContactCardAsync(Guid contactId, CancellationToken cancellationToken)
    {
        var contact = await crmService.GetContactAsync(contactId, cancellationToken);
        if (contact is null || contact.TrashedAt is not null)
            return SentinelLiveCard.Unavailable(SentinelLiveBlockKinds.Contact, "This contact no longer exists.");
        var deals = await crmService.ListDealsForContactAsync(contactId, cancellationToken);
        var openDeals = deals.Where(deal => deal.Stage is not (DealStages.Won or DealStages.Lost)).ToList();
        var openTickets = (await ticketService.ListTicketsForContactAsync(contactId, cancellationToken))
            .Count(ticket => ticket.Status is not (SupportTicketStatuses.Resolved or SupportTicketStatuses.Closed));
        var fields = new List<SentinelLiveField> { new("Status", contact.Status) };
        if (!string.IsNullOrWhiteSpace(contact.Email)) fields.Add(new("Email", contact.Email));
        if (contact.FollowUpDate is { } followUp) fields.Add(new("Follow up", Day(followUp)));
        fields.Add(new("Open deals", openDeals.Count == 0 ? "None" : $"{openDeals.Count} · {Usd(openDeals.Sum(deal => deal.ValueUsd))}"));
        fields.Add(new("Open tickets", openTickets == 0 ? "None" : openTickets.ToString(Invariant)));
        return new SentinelLiveCard(SentinelLiveBlockKinds.Contact, contact.FullName, contact.Company,
            $"/admin/crm/contacts/{contact.Id}", fields, [], [], Now());
    }

    private async Task<SentinelLiveCard> DealCardAsync(Guid dealId, CancellationToken cancellationToken)
    {
        var deal = (await crmService.ListDealsAsync(cancellationToken)).FirstOrDefault(item => item.Id == dealId);
        if (deal is null)
            return SentinelLiveCard.Unavailable(SentinelLiveBlockKinds.Deal, "This deal no longer exists.");
        var fields = new List<SentinelLiveField>
        {
            new("Stage", StageName(deal.Stage)),
            new("Value", Usd(deal.ValueUsd)),
            new("Contact", deal.ContactName)
        };
        if (deal.ClosedAt is { } closed) fields.Add(new("Closed", Day(closed)));
        else if (deal.ExpectedCloseDate is { } expected) fields.Add(new("Expected close", Day(expected)));
        return new SentinelLiveCard(SentinelLiveBlockKinds.Deal, deal.Title, null,
            $"/admin/crm/contacts/{deal.ContactId}", fields, [], [], Now());
    }

    private async Task<SentinelLiveCard> TicketCardAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await ticketService.GetTicketAsync(ticketId, cancellationToken);
        if (ticket is null)
            return SentinelLiveCard.Unavailable(SentinelLiveBlockKinds.Ticket, "This ticket no longer exists.");
        var fields = new List<SentinelLiveField>
        {
            new("Status", ticket.Status),
            new("Priority", ticket.Priority),
            new("Assigned to", string.IsNullOrWhiteSpace(ticket.AssignedToUsername) ? "Nobody" : ticket.AssignedToUsername),
            new("Messages", ticket.Messages.Count.ToString(Invariant))
        };
        if (ticket.LastRepliedAt is { } replied) fields.Add(new("Last reply", When(replied)));
        if (ticket.ResolvedAt is { } resolved) fields.Add(new("Resolved", When(resolved)));
        else if (ticket.ResolutionDueAt is { } due) fields.Add(new("Resolve by", When(due)));
        return new SentinelLiveCard(SentinelLiveBlockKinds.Ticket, ticket.Subject, ticket.ContactName,
            "/admin/support", fields, [], [], Now());
    }

    private async Task<SentinelLiveCard> ChartCardAsync(Guid widgetId, string username, CancellationToken cancellationToken)
    {
        var widget = (await biService.GetDashboardAsync(username, cancellationToken: cancellationToken)).FirstOrDefault(item => item.Id == widgetId);
        if (widget is null)
            return SentinelLiveCard.Unavailable(SentinelLiveBlockKinds.Chart, "This chart isn't on your BI dashboard any more.");
        var chart = widget.Chart;
        var fields = new List<SentinelLiveField> { new("Total", BiValue(chart.Total, chart.ValueFormat)) };
        if (chart.PreviousTotal != 0)
        {
            var change = (chart.Total - chart.PreviousTotal) / Math.Abs(chart.PreviousTotal);
            fields.Add(new("vs previous period", change.ToString("+0%;-0%;0%", Invariant)));
        }
        if (widget.GoalValue is { } goal) fields.Add(new(widget.GoalIsCeiling ? "Limit" : "Target", BiValue(goal, chart.ValueFormat)));
        var largest = chart.Points.Count == 0 ? 0m : chart.Points.Max(point => Math.Abs(point.Value));
        var bars = chart.Points
            .OrderByDescending(point => point.Value)
            .Take(MaxBars)
            .Select(point => new SentinelLiveBar(point.Label, BiValue(point.Value, chart.ValueFormat),
                largest == 0 ? 0 : (double)(Math.Abs(point.Value) / largest)))
            .ToList();
        return new SentinelLiveCard(SentinelLiveBlockKinds.Chart, widget.Title,
            $"{chart.MetricLabel} by {chart.DimensionLabel} · {Day(chart.From)} to {Day(chart.To)}",
            "/admin/business-intelligence", fields, bars, [], Now());
    }

    private async Task<SentinelLiveCard> CalendarCardAsync(CancellationToken cancellationToken)
    {
        var (from, to) = ThisWeek();
        var items = (await calendarService.GetItemsAsync(from.ToUniversalTime(), to.ToUniversalTime(), cancellationToken))
            .OrderBy(item => item.At.UtcTicks)
            .ToList();
        var rows = items
            .Select(item => new SentinelLiveRow(Local(item.At).ToString("ddd dd-MM", Invariant),
                $"{Local(item.At).ToString("HH:mm", Invariant)} {item.Title}", $"{item.Kind} · {item.Status}",
                string.IsNullOrWhiteSpace(item.Link) ? null : item.Link))
            .ToList();
        return new SentinelLiveCard(SentinelLiveBlockKinds.Calendar, "Content calendar",
            $"This week · {Day(from)} to {Day(to.AddDays(-1))}", "/admin/content-calendar",
            [new("Scheduled", items.Count == 0 ? "Nothing this week" : items.Count.ToString(Invariant))], [], rows, Now());
    }

    private async Task<SentinelLiveCard> TimeCardAsync(CancellationToken cancellationToken)
    {
        var (from, to) = ThisWeek();
        var entries = await timeTrackingService.ListAsync(from.ToUniversalTime(), to.ToUniversalTime(), cancellationToken: cancellationToken);
        var totalMinutes = entries.Sum(entry => entry.Minutes);
        var billableMinutes = entries.Where(entry => entry.Billable).Sum(entry => entry.Minutes);
        var byContact = entries
            .GroupBy(entry => entry.ContactName)
            .Select(group => (Name: group.Key, Minutes: group.Sum(entry => entry.Minutes)))
            .OrderByDescending(group => group.Minutes)
            .Take(MaxBars)
            .ToList();
        var largest = byContact.Count == 0 ? 0 : byContact.Max(group => group.Minutes);
        var bars = byContact
            .Select(group => new SentinelLiveBar(group.Name, Hours(group.Minutes), largest == 0 ? 0 : (double)group.Minutes / largest))
            .ToList();
        var fields = new List<SentinelLiveField>
        {
            new("Total", Hours(totalMinutes)),
            new("Billable", Hours(billableMinutes)),
            new("Entries", entries.Count.ToString(Invariant))
        };
        if (entries.Any(entry => entry.EndedAt is null)) fields.Add(new("Running", "A timer is running"));
        return new SentinelLiveCard(SentinelLiveBlockKinds.Time, "Time tracking",
            $"This week · {Day(from)} to {Day(to.AddDays(-1))}", "/admin/time", fields, bars, [], Now());
    }

    // Monday 00:00 local to the next Monday 00:00.
    private (DateTimeOffset From, DateTimeOffset To) ThisWeek()
    {
        var now = timeProvider.GetLocalNow();
        var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        var monday = new DateTimeOffset(now.Date.AddDays(-daysSinceMonday), now.Offset);
        return (monday, monday.AddDays(7));
    }

    private string Now() => timeProvider.GetLocalNow().ToString("HH:mm", Invariant);

    // Shown in the TimeProvider's zone (the server's local zone in the app) so the week and the
    // dates on a card always agree.
    private DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, timeProvider.LocalTimeZone);

    private string Day(DateTimeOffset value) => Local(value).ToString("dd-MM-yy", Invariant);

    private string When(DateTimeOffset value) => Local(value).ToString("dd-MM-yy HH:mm", Invariant);

    private static string Usd(decimal value) => "$" + value.ToString("N0", Invariant);

    private static string Hours(int minutes) => $"{minutes / 60}h {minutes % 60:00}m";

    private static string BiValue(decimal value, string format) => format switch
    {
        "Currency" => "$" + value.ToString("N2", Invariant),
        "Rating" => value.ToString("0.0#", Invariant) + " / 5",
        _ => value.ToString("N0", Invariant)
    };

    private static string StageName(string stage) => stage == DealStages.ProposalSent ? "Proposal sent" : stage;
}
