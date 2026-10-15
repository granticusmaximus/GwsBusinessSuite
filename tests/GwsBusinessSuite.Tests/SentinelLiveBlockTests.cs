using System.Reflection;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.BusinessIntelligence;
using GwsBusinessSuite.Application.ContentCalendar;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Application.TimeTracking;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel item 5: live business blocks.
public sealed class SentinelLiveBlockTests
{
    // Wednesday 14-10-26 15:00 UTC; the clock's local zone is UTC, so this week is 12-10 to 19-10.
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Monday = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // Answers the named interface methods; anything else fails the test loudly.
    public class Stub<T> : DispatchProxy where T : class
    {
        private readonly Dictionary<string, Func<object?[], object?>> _handlers = [];

        public static (T Instance, Stub<T> Stub) New()
        {
            var instance = Create<T, Stub<T>>();
            return (instance, (Stub<T>)(object)instance);
        }

        public Stub<T> On(string method, Func<object?[], object?> handler)
        {
            _handlers[method] = handler;
            return this;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handlers.TryGetValue(targetMethod!.Name, out var handler)
                ? handler(args ?? [])
                : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod.Name} wasn't expected.");
    }

    private sealed class Fixture
    {
        public required ApplicationDbContext Db { get; init; }
        public required ICrmService Crm { get; init; }
        public List<SupportTicketView> Tickets { get; } = [];
        public List<BiDashboardWidget> Widgets { get; } = [];
        public List<ContentCalendarItem> CalendarItems { get; } = [];
        public List<TimeEntryView> TimeEntries { get; } = [];
        public List<(DateTimeOffset From, DateTimeOffset To)> RangesAsked { get; } = [];
        public required SentinelLiveBlockService Service { get; set; }
    }

    private static async Task<Fixture> FixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var fixture = new Fixture { Db = db, Crm = new CrmService(db), Service = null! };

        var (tickets, ticketStub) = Stub<ISupportTicketService>.New();
        ticketStub
            .On(nameof(ISupportTicketService.ListTicketsAsync), _ => Task.FromResult<IReadOnlyList<SupportTicketView>>(fixture.Tickets.ToList()))
            .On(nameof(ISupportTicketService.ListTicketsForContactAsync), args =>
                Task.FromResult<IReadOnlyList<SupportTicketView>>(fixture.Tickets.Where(t => t.ContactId == (Guid)args[0]!).ToList()))
            .On(nameof(ISupportTicketService.GetTicketAsync), args => Task.FromResult(fixture.Tickets.FirstOrDefault(t => t.Id == (Guid)args[0]!)));
        var (bi, biStub) = Stub<IBusinessIntelligenceService>.New();
        biStub.On(nameof(IBusinessIntelligenceService.GetDashboardAsync), args =>
            Task.FromResult<IReadOnlyList<BiDashboardWidget>>((string)args[0]! == "grant" ? fixture.Widgets.ToList() : []));
        var (calendar, calendarStub) = Stub<IContentCalendarService>.New();
        calendarStub.On(nameof(IContentCalendarService.GetItemsAsync), args =>
        {
            fixture.RangesAsked.Add(((DateTimeOffset)args[0]!, (DateTimeOffset)args[1]!));
            return Task.FromResult<IReadOnlyList<ContentCalendarItem>>(fixture.CalendarItems.ToList());
        });
        var (time, timeStub) = Stub<ITimeTrackingService>.New();
        timeStub.On(nameof(ITimeTrackingService.ListAsync), args =>
        {
            fixture.RangesAsked.Add(((DateTimeOffset)args[0]!, (DateTimeOffset)args[1]!));
            return Task.FromResult<IReadOnlyList<TimeEntryView>>(fixture.TimeEntries.ToList());
        });
        fixture.Service = new SentinelLiveBlockService(fixture.Crm, tickets, bi, calendar, time, new UtcClock());
        return fixture;
    }

    private static async Task<Contact> ContactAsync(Fixture f, string name = "Sam Client")
    {
        var contact = new Contact { FullName = name, Email = $"{name.Split(' ')[0].ToLowerInvariant()}@example.com", Company = "Acme", Status = ContactStatuses.Lead, CreatedBy = "grant" };
        f.Db.Contacts.Add(contact);
        await f.Db.SaveChangesAsync();
        return contact;
    }

    private static async Task<Deal> DealAsync(Fixture f, Guid contactId, string title, string stage, decimal value)
    {
        var deal = new Deal { ContactId = contactId, Title = title, Stage = stage, ValueUsd = value, CreatedBy = "grant" };
        f.Db.Deals.Add(deal);
        await f.Db.SaveChangesAsync();
        return deal;
    }

    private static SupportTicketView Ticket(Guid contactId, string subject, string status) =>
        new(Guid.NewGuid(), contactId, "Sam Client", subject, status, "High", "grant", Now.AddHours(-2), null, [],
            Now.AddDays(-1), string.Empty, null, Now.AddDays(1), null, null);

    private static string? Field(SentinelLiveCard card, string label) => card.Fields.FirstOrDefault(f => f.Label == label)?.Value;

    [Fact]
    public async Task ContactCard_ShouldShowOpenDealsAndTickets_AndFollowChangesToTheRecord()
    {
        var f = await FixtureAsync();
        var contact = await ContactAsync(f);
        await DealAsync(f, contact.Id, "Website", DealStages.Negotiation, 4000m);
        await DealAsync(f, contact.Id, "Old job", DealStages.Won, 9000m);
        f.Tickets.Add(Ticket(contact.Id, "Login broken", SupportTicketStatuses.Open));
        f.Tickets.Add(Ticket(contact.Id, "Invoice question", SupportTicketStatuses.Resolved));

        var card = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Contact, contact.Id.ToString(), "grant");

        card.Error.Should().BeNull();
        card.Title.Should().Be("Sam Client");
        card.Subtitle.Should().Be("Acme");
        card.Link.Should().Be($"/admin/crm/contacts/{contact.Id}");
        Field(card, "Open deals").Should().Be("1 · $4,000");
        Field(card, "Open tickets").Should().Be("1");
        card.AsOf.Should().Be("15:00");

        // Nothing is cached: the next read shows the change made elsewhere.
        contact.Status = ContactStatuses.Customer;
        await f.Db.SaveChangesAsync();
        await DealAsync(f, contact.Id, "Retainer", DealStages.Qualified, 1500m);
        var refreshed = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Contact, contact.Id.ToString(), "grant");
        Field(refreshed, "Status").Should().Be(ContactStatuses.Customer);
        Field(refreshed, "Open deals").Should().Be("2 · $5,500");

        f.Db.Contacts.Remove(contact);
        await f.Db.SaveChangesAsync();
        (await f.Service.GetCardAsync(SentinelLiveBlockKinds.Contact, contact.Id.ToString(), "grant")).Error
            .Should().Be("This contact no longer exists.");
    }

    [Fact]
    public async Task DealCard_ShouldShowTheCurrentStage_AfterItMoves()
    {
        var f = await FixtureAsync();
        var contact = await ContactAsync(f);
        var deal = await DealAsync(f, contact.Id, "Website", DealStages.ProposalSent, 4000m);

        var card = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Deal, deal.Id.ToString(), "grant");
        card.Title.Should().Be("Website");
        Field(card, "Stage").Should().Be("Proposal sent");
        Field(card, "Value").Should().Be("$4,000");
        Field(card, "Contact").Should().Be("Sam Client");
        card.Link.Should().Be($"/admin/crm/contacts/{contact.Id}");

        await f.Crm.SetDealStageAsync(deal.Id, DealStages.Won, "grant");
        var moved = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Deal, deal.Id.ToString(), "grant");
        Field(moved, "Stage").Should().Be(DealStages.Won);
        Field(moved, "Closed").Should().NotBeNull();

        (await f.Service.GetCardAsync(SentinelLiveBlockKinds.Deal, Guid.NewGuid().ToString(), "grant")).Error
            .Should().Be("This deal no longer exists.");
    }

    [Fact]
    public async Task TicketCard_ShouldShowStatusAssigneeAndDueTimes()
    {
        var f = await FixtureAsync();
        var ticket = Ticket(Guid.NewGuid(), "Login broken", SupportTicketStatuses.Pending);
        f.Tickets.Add(ticket);

        var card = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Ticket, ticket.Id.ToString(), "grant");

        card.Title.Should().Be("Login broken");
        card.Subtitle.Should().Be("Sam Client");
        Field(card, "Status").Should().Be(SupportTicketStatuses.Pending);
        Field(card, "Assigned to").Should().Be("grant");
        Field(card, "Last reply").Should().Be("14-10-26 13:00");
        Field(card, "Resolve by").Should().Be("15-10-26 15:00");
    }

    [Fact]
    public async Task ChartCard_ShouldUseTheViewersOwnDashboard_WithBarsLargestFirst()
    {
        var f = await FixtureAsync();
        var chart = new BiChartResult("Revenue", "Month", "Currency", 3000m, Now.AddDays(-30), Now,
            [new BiDataPoint("Sep", 1000m), new BiDataPoint("Oct", 2000m)], PreviousTotal: 2000m);
        var widget = new BiDashboardWidget(Guid.NewGuid(), "Revenue by month", "q", "revenue", "month", "bar", 30, 0, chart);
        f.Widgets.Add(widget);

        var card = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Chart, widget.Id.ToString(), "grant");

        card.Title.Should().Be("Revenue by month");
        Field(card, "Total").Should().Be("$3,000.00");
        Field(card, "vs previous period").Should().Be("+50%");
        card.Bars.Select(bar => (bar.Label, bar.Value, bar.Share)).Should().Equal(("Oct", "$2,000.00", 1.0), ("Sep", "$1,000.00", 0.5));

        // Another Admin's dashboard doesn't have this widget.
        (await f.Service.GetCardAsync(SentinelLiveBlockKinds.Chart, widget.Id.ToString(), "someone-else")).Error
            .Should().Be("This chart isn't on your BI dashboard any more.");
        (await f.Service.SearchAsync(SentinelLiveBlockKinds.Chart, "revenue", "grant")).Should().ContainSingle()
            .Which.Id.Should().Be(widget.Id.ToString());
    }

    [Fact]
    public async Task CalendarAndTimeCards_ShouldCoverMondayToMonday_OfTheCurrentWeek()
    {
        var f = await FixtureAsync();
        f.CalendarItems.Add(new ContentCalendarItem("Article", Guid.NewGuid(), "Launch post", Monday.AddDays(3).AddHours(9), "Scheduled", "/admin/articles/1", true, true));
        var contactId = Guid.NewGuid();
        f.TimeEntries.Add(new TimeEntryView(Guid.NewGuid(), "grant", contactId, "Acme", null, null, null, null, "Design", Monday, Monday.AddMinutes(90), 90, true, 100m, null));
        f.TimeEntries.Add(new TimeEntryView(Guid.NewGuid(), "grant", Guid.NewGuid(), "Beta", null, null, null, null, "Call", Monday, Monday.AddMinutes(30), 30, false, 0m, null));

        var calendar = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Calendar, null, "grant");
        var time = await f.Service.GetCardAsync(SentinelLiveBlockKinds.Time, null, "grant");

        f.RangesAsked.Should().AllSatisfy(range => range.Should().Be((Monday, Monday.AddDays(7))));
        calendar.Subtitle.Should().Be("This week · 12-10-26 to 18-10-26");
        calendar.Rows.Should().ContainSingle().Which.Should().Be(new SentinelLiveRow("Thu 15-10", "09:00 Launch post", "Article · Scheduled", "/admin/articles/1"));
        Field(time, "Total").Should().Be("2h 00m");
        Field(time, "Billable").Should().Be("1h 30m");
        time.Bars.Select(bar => (bar.Label, bar.Value)).Should().Equal(("Acme", "1h 30m"), ("Beta", "0h 30m"));
    }

    [Fact]
    public async Task Search_ShouldMatchNameEmailOrCompany_AndCardsShouldRefuseBadInput()
    {
        var f = await FixtureAsync();
        await ContactAsync(f, "Sam Client");
        await ContactAsync(f, "Pat Other");

        (await f.Service.SearchAsync(SentinelLiveBlockKinds.Contact, "sam", "grant")).Select(t => t.Label).Should().Equal("Sam Client");
        (await f.Service.SearchAsync(SentinelLiveBlockKinds.Contact, "acme", "grant")).Should().HaveCount(2);
        (await f.Service.SearchAsync(SentinelLiveBlockKinds.Calendar, null, "grant")).Should().BeEmpty();
        (await f.Service.GetCardAsync(SentinelLiveBlockKinds.Contact, "", "grant")).Error.Should().Be("Choose what to show first.");
        (await f.Service.GetCardAsync("payroll", null, "grant")).Error.Should().Be("This kind of live block isn't supported.");
    }

    [Fact]
    public void StaticRenders_ShouldNeverIncludeLiveData_OnlyTheLabel()
    {
        var block = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Business, 0, [],
            new Dictionary<string, string> { ["kind"] = SentinelLiveBlockKinds.Contact, ["recordId"] = Guid.NewGuid().ToString(), ["recordLabel"] = "Sam Client" });
        var week = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Business, 0, [], new Dictionary<string, string> { ["kind"] = SentinelLiveBlockKinds.Time });
        var paragraph = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Before")], new Dictionary<string, string>());

        var html = WikiBlockHtmlRenderer.RenderPage([paragraph, block, week]);
        html.Should().Contain("Before").And.NotContain("Sam Client").And.NotContain("Live");

        WikiBlockHtmlRenderer.PlainTextPreview(block).Should().Be("Live CRM contact: Sam Client");
        WikiBlockHtmlRenderer.PlainTextPreview(week).Should().Be("Live Time tracking (this week)");
        WikiMarkdownExporter.ExportPage("Page", [block]).Should().Contain("*[Live CRM contact: Sam Client]*");
    }
}
