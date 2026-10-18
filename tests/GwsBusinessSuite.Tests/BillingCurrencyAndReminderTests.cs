using FluentAssertions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Billing phase 2: invoice currencies, duplicating finished invoices, and payment reminders.
public sealed class BillingCurrencyAndReminderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(12.34, "usd", 1234)]
    [InlineData(0.005, "eur", 1)]
    [InlineData(1234, "jpy", 1234)]
    [InlineData(1234.6, "krw", 1235)]
    public void MinorUnits_ShouldFollowEachCurrencysDecimals(decimal amount, string currency, long expected) =>
        InvoiceCurrencies.ToMinorUnits(amount, currency).Should().Be(expected);

    [Fact]
    public void Format_ShouldUseTheCodeNotADollarSign()
    {
        InvoiceCurrencies.Format(1234.5m, "eur").Should().Be("EUR 1,234.50");
        InvoiceCurrencies.Format(1234m, "jpy").Should().Be("JPY 1,234");
    }

    [Fact]
    public async Task Draft_ShouldKeepItsCurrency_SendItToStripe_AndRejectUnknownOnes()
    {
        await using var f = await Fixture.CreateAsync();
        var contact = await f.AddContactAsync();
        var draft = await f.Service.SaveDraftAsync(Editor(contact.Id, "EUR"));

        draft.Currency.Should().Be("eur");
        draft.TotalText.Should().Be("EUR 1,000.00");
        await f.Service.SendInvoiceAsync(draft.Id);
        f.Stripe.SentCurrencies.Should().Equal("eur");

        await f.Service.Invoking(service => service.SaveDraftAsync(Editor(contact.Id, "xyz")))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Duplicate_ShouldCopyAnyInvoiceIntoANewDraft()
    {
        await using var f = await Fixture.CreateAsync();
        var contact = await f.AddContactAsync();
        var original = await f.Service.SaveDraftAsync(Editor(contact.Id, "gbp", autoRemind: true));
        await f.Service.SendInvoiceAsync(original.Id);
        await f.Service.MarkInvoicePaidByStripeIdAsync(f.Stripe.LastId!);

        var copy = await f.Service.DuplicateAsync(original.Id);

        copy.Id.Should().NotBe(original.Id);
        copy.Status.Should().Be(InvoiceStatuses.Draft);
        copy.Title.Should().Be("Copy of Retainer");
        copy.Currency.Should().Be("gbp");
        copy.AutoRemind.Should().BeTrue();
        copy.DueDate.Should().BeNull();
        copy.LineItems.Select(line => (line.Description, line.Quantity, line.UnitPriceUsd))
            .Should().Equal(("Design", 2, 400m), ("Hosting", 1, 200m));
        (await f.Service.GetInvoiceAsync(original.Id))!.Status.Should().Be(InvoiceStatuses.Paid, "the original is untouched");
        (await f.Service.DuplicateAsync(copy.Id)).Title.Should().Be("Copy of Retainer", "no 'Copy of Copy of'");
    }

    [Fact]
    public async Task ManualReminder_ShouldResendASentInvoice_AtMostOncePerDay()
    {
        await using var f = await Fixture.CreateAsync();
        var contact = await f.AddContactAsync();
        var draft = await f.Service.SaveDraftAsync(Editor(contact.Id, "usd"));

        await f.Service.Invoking(service => service.SendReminderAsync(draft.Id))
            .Should().ThrowAsync<InvalidOperationException>("a draft hasn't been sent");

        await f.Service.SendInvoiceAsync(draft.Id);
        var reminded = await f.Service.SendReminderAsync(draft.Id);
        reminded.RemindersSent.Should().Be(1);
        f.Stripe.Resent.Should().ContainSingle();

        await f.Service.Invoking(service => service.SendReminderAsync(draft.Id))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*last day*");
        f.Clock.Advance(TimeSpan.FromDays(1));
        (await f.Service.SendReminderAsync(draft.Id)).RemindersSent.Should().Be(2);
    }

    [Fact]
    public async Task AutoReminders_ShouldStartADayAfterDue_RepeatWeekly_AndStopAtThree()
    {
        await using var f = await Fixture.CreateAsync();
        var contact = await f.AddContactAsync();
        var auto = await f.Service.SaveDraftAsync(Editor(contact.Id, "usd", autoRemind: true, dueInDays: 10));
        var manual = await f.Service.SaveDraftAsync(Editor(contact.Id, "usd", autoRemind: false, dueInDays: 10));
        await f.Service.SendInvoiceAsync(auto.Id);
        await f.Service.SendInvoiceAsync(manual.Id);

        f.Clock.Advance(TimeSpan.FromDays(10.5));
        (await f.Service.SendDueRemindersAsync()).Should().Be(0, "not yet a day overdue");

        f.Clock.Advance(TimeSpan.FromDays(1));
        (await f.Service.SendDueRemindersAsync()).Should().Be(1, "only the auto-remind invoice");
        f.Clock.Advance(TimeSpan.FromDays(3));
        (await f.Service.SendDueRemindersAsync()).Should().Be(0, "a week between reminders");

        for (var week = 0; week < 5; week++)
        {
            f.Clock.Advance(TimeSpan.FromDays(7));
            await f.Service.SendDueRemindersAsync();
        }
        (await f.Service.GetInvoiceAsync(auto.Id))!.RemindersSent.Should().Be(3);
        (await f.Service.GetInvoiceAsync(manual.Id))!.RemindersSent.Should().Be(0);

        // Paid invoices are never reminded.
        var paid = await f.Service.SaveDraftAsync(Editor(contact.Id, "usd", autoRemind: true, dueInDays: 30));
        await f.Service.SendInvoiceAsync(paid.Id);
        await f.Service.MarkInvoicePaidByStripeIdAsync(f.Stripe.LastId!);
        f.Clock.Advance(TimeSpan.FromDays(40));
        await f.Service.SendDueRemindersAsync();
        (await f.Service.GetInvoiceAsync(paid.Id))!.RemindersSent.Should().Be(0);
    }

    private InvoiceEditorModel Editor(Guid contactId, string currency, bool autoRemind = false, int? dueInDays = null) => new()
    {
        ContactId = contactId,
        Title = "Retainer",
        Currency = currency,
        AutoRemind = autoRemind,
        DueDate = dueInDays is { } days ? Start.AddDays(days) : null,
        LineItems =
        [
            new InvoiceLineItemEditorModel { Description = "Design", Quantity = 2, UnitPriceUsd = 400m },
            new InvoiceLineItemEditorModel { Description = "Hosting", Quantity = 1, UnitPriceUsd = 200m }
        ]
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ApplicationDbContext _db;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            _db = db;
            Service = new BillingService(db, Stripe, Clock);
        }

        public RecordingStripe Stripe { get; } = new();
        public ManualClock Clock { get; } = new(Start);
        public BillingService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<Contact> AddContactAsync()
        {
            var contact = new Contact { FullName = "Jamie Rivera", Email = "jamie@example.test" };
            _db.Contacts.Add(contact);
            await _db.SaveChangesAsync();
            return contact;
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingStripe : IStripeInvoicingClient
    {
        private int _count;
        public bool IsConfigured => true;
        public List<string> SentCurrencies { get; } = [];
        public List<string> Resent { get; } = [];
        public string? LastId { get; private set; }

        public Task<string> EnsureCustomerAsync(string? existingStripeCustomerId, string contactName, string? contactEmail, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(existingStripeCustomerId ?? "cus_1");

        public Task<StripeSentInvoice> CreateAndSendInvoiceAsync(string stripeCustomerId, string currency, int daysUntilDue, IReadOnlyList<InvoiceLineItemView> lineItems, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            SentCurrencies.Add(currency);
            LastId = $"in_{++_count}";
            return Task.FromResult(new StripeSentInvoice(LastId, $"https://stripe.test/{LastId}"));
        }

        public Task VoidInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResendInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default)
        {
            Resent.Add(stripeInvoiceId);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
