using FluentAssertions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Application.TimeTracking;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class TimeTrackingServiceTests
{
    [Fact]
    public async Task Timer_ShouldRunOnePerUser_AndStartingAnotherStopsTheFirst()
    {
        await using var f = await Fixture.CreateAsync();
        var acme = await f.AddContactAsync("Acme");
        var beta = await f.AddContactAsync("Beta");

        await f.Service.StartAsync("grant", new TimeEntryInput { ContactId = acme.Id, Description = "Kickoff", HourlyRateUsd = 120 });
        f.Clock.Advance(TimeSpan.FromMinutes(45));
        var second = await f.Service.StartAsync("grant", new TimeEntryInput { ContactId = beta.Id, HourlyRateUsd = 100 });

        (await f.Service.GetRunningAsync("grant"))!.Id.Should().Be(second.Id);
        var acmeEntries = await f.Service.ListForContactAsync(acme.Id);
        acmeEntries.Should().ContainSingle().Which.Minutes.Should().Be(45, "starting a new timer stopped the first one");

        f.Clock.Advance(TimeSpan.FromSeconds(10));
        var stopped = await f.Service.StopAsync("grant");
        stopped!.Minutes.Should().Be(1, "a stopped timer counts at least a minute");
        (await f.Service.GetRunningAsync("grant")).Should().BeNull();
        (await f.Service.StopAsync("grant")).Should().BeNull();
    }

    [Fact]
    public async Task BillUnbilled_ShouldPutEachBillableEntryOnADraft_AndReleaseThemWhenTheDraftIsDeleted()
    {
        await using var f = await Fixture.CreateAsync();
        var acme = await f.AddContactAsync("Acme");
        var ticket = new SupportTicket { ContactId = acme.Id, Subject = "Login trouble" };
        f.Db.SupportTickets.Add(ticket);
        await f.Db.SaveChangesAsync();

        await f.Service.AddManualAsync("grant", new TimeEntryInput { ContactId = acme.Id, TicketId = ticket.Id, Description = "Fixed SSO", Minutes = 90, HourlyRateUsd = 120, StartedAt = f.Clock.GetUtcNow() });
        await f.Service.AddManualAsync("grant", new TimeEntryInput { ContactId = acme.Id, Description = "Call", Minutes = 20, HourlyRateUsd = 100, StartedAt = f.Clock.GetUtcNow().AddHours(2) });
        await f.Service.AddManualAsync("grant", new TimeEntryInput { ContactId = acme.Id, Description = "Internal", Minutes = 60, Billable = false, StartedAt = f.Clock.GetUtcNow() });

        (await f.Service.ListUnbilledAsync()).Should().ContainSingle().Which.Should().Match<UnbilledTimeSummary>(s =>
            s.EntryCount == 2 && s.Minutes == 110 && s.AmountUsd == 213.33m);

        var invoice = await f.Service.BillUnbilledAsync(acme.Id, "grant");

        invoice.Status.Should().Be(InvoiceStatuses.Draft);
        invoice.LineItems.Should().HaveCount(2);
        invoice.LineItems.Select(l => l.UnitPriceUsd).Should().Equal(180m, 33.33m);
        invoice.LineItems[0].Description.Should().Contain("Support: Login trouble").And.Contain("Fixed SSO").And.EndWith("1.5 h @ $120/h");
        (await f.Service.ListUnbilledAsync()).Should().BeEmpty();
        await FluentActions.Awaiting(() => f.Service.BillUnbilledAsync(acme.Id, "grant"))
            .Should().ThrowAsync<InvalidOperationException>("the same hours can't be billed twice");
        var billed = (await f.Service.ListForContactAsync(acme.Id)).First(e => e.Billable);
        await FluentActions.Awaiting(() => f.Service.DeleteAsync(billed.Id)).Should().ThrowAsync<InvalidOperationException>();

        await f.Billing.DeleteDraftAsync(invoice.Id, "grant");

        (await f.Service.ListUnbilledAsync()).Should().ContainSingle().Which.EntryCount.Should().Be(2,
            "deleting the draft puts its hours back to unbilled");
    }

    [Fact]
    public async Task Entries_ShouldRefuseADealOrTicketFromAnotherContact()
    {
        await using var f = await Fixture.CreateAsync();
        var acme = await f.AddContactAsync("Acme");
        var beta = await f.AddContactAsync("Beta");
        var betaDeal = new Deal { ContactId = beta.Id, Title = "Beta deal" };
        f.Db.Deals.Add(betaDeal);
        await f.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => f.Service.StartAsync("grant", new TimeEntryInput { ContactId = acme.Id, DealId = betaDeal.Id }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*different contact*");
        await FluentActions.Awaiting(() => f.Service.AddManualAsync("grant", new TimeEntryInput { ContactId = acme.Id, Minutes = 0, StartedAt = f.Clock.GetUtcNow() }))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetLastRate_ShouldPrefillFromTheUsersMostRecentEntry()
    {
        await using var f = await Fixture.CreateAsync();
        var acme = await f.AddContactAsync("Acme");
        (await f.Service.GetLastRateAsync("grant")).Should().Be(0);

        await f.Service.AddManualAsync("grant", new TimeEntryInput { ContactId = acme.Id, Minutes = 30, HourlyRateUsd = 95, StartedAt = f.Clock.GetUtcNow() });
        await f.Service.AddManualAsync("grant", new TimeEntryInput { ContactId = acme.Id, Minutes = 30, HourlyRateUsd = 150, StartedAt = f.Clock.GetUtcNow().AddDays(1) });

        (await f.Service.GetLastRateAsync("grant")).Should().Be(150);
        (await f.Service.GetLastRateAsync("someone-else")).Should().Be(0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        public ApplicationDbContext Db { get; private set; } = null!;
        public Clock Clock { get; } = new(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero));
        public BillingService Billing { get; private set; } = null!;
        public TimeTrackingService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture { _connection = new SqliteConnection("Data Source=:memory:") };
            await f._connection.OpenAsync();
            f.Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f._connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Billing = new BillingService(f.Db, new NoStripe(), f.Clock);
            f.Service = new TimeTrackingService(f.Db, f.Billing, f.Clock);
            return f;
        }

        public async Task<Contact> AddContactAsync(string name)
        {
            var contact = new Contact { FullName = name };
            Db.Contacts.Add(contact);
            await Db.SaveChangesAsync();
            return contact;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoStripe : IStripeInvoicingClient
    {
        public bool IsConfigured => false;
        public Task<string> EnsureCustomerAsync(string? existingStripeCustomerId, string contactName, string? contactEmail, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StripeSentInvoice> CreateAndSendInvoiceAsync(string stripeCustomerId, string currency, int daysUntilDue, IReadOnlyList<InvoiceLineItemView> lineItems, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task VoidInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
