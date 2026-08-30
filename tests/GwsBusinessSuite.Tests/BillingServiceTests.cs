using FluentAssertions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class BillingServiceTests
{
    [Fact]
    public async Task SaveDraftAsync_ShouldRejectAnInvoiceWithNoLineItems_WhenSendIsAttempted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "October retainer"
        });

        draft.Status.Should().Be(InvoiceStatuses.Draft);
        fixture.Stripe.IsConfigured = true;

        var act = () => fixture.Service.SendInvoiceAsync(draft.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendInvoiceAsync_ShouldThrow_WhenStripeIsNotConfigured()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "October retainer",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Consulting", Quantity = 1, UnitPriceUsd = 500m }]
        });

        fixture.Stripe.IsConfigured = false;
        var act = () => fixture.Service.SendInvoiceAsync(draft.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendInvoiceAsync_ShouldCreateAStripeCustomerOnce_AndReuseItOnASecondInvoice()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        fixture.Stripe.IsConfigured = true;

        var first = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice 1",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Consulting", Quantity = 2, UnitPriceUsd = 150m }]
        });
        var sentFirst = await fixture.Service.SendInvoiceAsync(first.Id);

        sentFirst.Status.Should().Be(InvoiceStatuses.Sent);
        sentFirst.TotalUsd.Should().Be(300m);
        sentFirst.StripeHostedInvoiceUrl.Should().NotBeNullOrWhiteSpace();
        fixture.Stripe.CustomersCreated.Should().Be(1);

        var second = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice 2",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Support", Quantity = 1, UnitPriceUsd = 75m }]
        });
        await fixture.Service.SendInvoiceAsync(second.Id);

        // The second invoice must reuse the Stripe customer minted for the first, not create
        // a duplicate - EnsureCustomerAsync is only called with a null existing id once.
        fixture.Stripe.CustomersCreated.Should().Be(1);
    }

    [Fact]
    public async Task SendInvoiceAsync_ShouldOnlyBeAllowed_FromDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        fixture.Stripe.IsConfigured = true;
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Work", Quantity = 1, UnitPriceUsd = 100m }]
        });
        await fixture.Service.SendInvoiceAsync(draft.Id);

        var act = () => fixture.Service.SendInvoiceAsync(draft.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task VoidInvoiceAsync_ShouldOnlyBeAllowed_FromSent_AndShouldVoidInStripe()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        fixture.Stripe.IsConfigured = true;
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Work", Quantity = 1, UnitPriceUsd = 100m }]
        });

        var voidBeforeSend = () => fixture.Service.VoidInvoiceAsync(draft.Id);
        await voidBeforeSend.Should().ThrowAsync<InvalidOperationException>();

        var sent = await fixture.Service.SendInvoiceAsync(draft.Id);
        var voided = await fixture.Service.VoidInvoiceAsync(sent.Id);

        voided.Status.Should().Be(InvoiceStatuses.Void);
        fixture.Stripe.VoidedInvoiceIds.Should().ContainSingle().Which.Should().Be(fixture.Stripe.LastCreatedStripeInvoiceId);
    }

    [Fact]
    public async Task MarkInvoicePaidByStripeIdAsync_ShouldBeIdempotent_OnWebhookRedelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        fixture.Stripe.IsConfigured = true;
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Work", Quantity = 1, UnitPriceUsd = 100m }]
        });
        var sent = await fixture.Service.SendInvoiceAsync(draft.Id);
        var stripeInvoiceId = fixture.Stripe.LastCreatedStripeInvoiceId!;

        await fixture.Service.MarkInvoicePaidByStripeIdAsync(stripeInvoiceId);
        var paidOnce = await fixture.Service.GetInvoiceAsync(sent.Id);
        paidOnce!.Status.Should().Be(InvoiceStatuses.Paid);
        var firstPaidAt = paidOnce.PaidAt;

        // A second webhook delivery for the same event must not throw or move PaidAt.
        await fixture.Service.MarkInvoicePaidByStripeIdAsync(stripeInvoiceId);
        var paidTwice = await fixture.Service.GetInvoiceAsync(sent.Id);
        paidTwice!.PaidAt.Should().Be(firstPaidAt);
    }

    [Fact]
    public async Task MarkInvoicePaidByStripeIdAsync_ShouldNotReviveAVoidedInvoice_OnADelayedWebhook()
    {
        // Regression test: Stripe doesn't guarantee ordered/immediate webhook delivery, so a
        // delayed or redelivered invoice.paid event can arrive after an admin has already voided
        // the invoice. The idempotency guard only checked for Paid, not Void, so this used to
        // silently flip a deliberately-voided invoice back to Paid.
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        fixture.Stripe.IsConfigured = true;
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Work", Quantity = 1, UnitPriceUsd = 100m }]
        });
        var sent = await fixture.Service.SendInvoiceAsync(draft.Id);
        var stripeInvoiceId = fixture.Stripe.LastCreatedStripeInvoiceId!;
        await fixture.Service.VoidInvoiceAsync(sent.Id);

        await fixture.Service.MarkInvoicePaidByStripeIdAsync(stripeInvoiceId);

        var afterDelayedWebhook = await fixture.Service.GetInvoiceAsync(sent.Id);
        afterDelayedWebhook!.Status.Should().Be(InvoiceStatuses.Void);
        afterDelayedWebhook.PaidAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldOnlyBeAllowed_ForADraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        fixture.Stripe.IsConfigured = true;
        var draft = await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Invoice",
            LineItems = [new InvoiceLineItemEditorModel { Description = "Work", Quantity = 1, UnitPriceUsd = 100m }]
        });
        var sent = await fixture.Service.SendInvoiceAsync(draft.Id);

        var act = () => fixture.Service.DeleteDraftAsync(sent.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await fixture.Service.DeleteDraftAsync((await fixture.Service.SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = contact.Id,
            Title = "Another draft"
        })).Id);
        (await fixture.Service.ListInvoicesForContactAsync(contact.Id)).Should().ContainSingle(item => item.Id == sent.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, FakeStripeInvoicingClient stripe)
        {
            _connection = connection;
            Db = db;
            Stripe = stripe;
            Service = new BillingService(db, stripe, new FixedTimeProvider(DateTimeOffset.UtcNow));
        }

        public ApplicationDbContext Db { get; }
        public FakeStripeInvoicingClient Stripe { get; }
        public BillingService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, new FakeStripeInvoicingClient());
        }

        public async Task<Contact> AddContactAsync(string fullName, string? email = null)
        {
            var contact = new Contact { FullName = fullName, Email = email };
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

    private sealed class FakeStripeInvoicingClient : IStripeInvoicingClient
    {
        public bool IsConfigured { get; set; }
        public int CustomersCreated { get; private set; }
        public string? LastCreatedStripeInvoiceId { get; private set; }
        public List<string> VoidedInvoiceIds { get; } = [];
        private int _invoiceCounter;

        public Task<string> EnsureCustomerAsync(
            string? existingStripeCustomerId, string contactName, string? contactEmail, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(existingStripeCustomerId))
            {
                return Task.FromResult(existingStripeCustomerId);
            }
            CustomersCreated++;
            return Task.FromResult($"cus_fake_{CustomersCreated}");
        }

        public Task<StripeSentInvoice> CreateAndSendInvoiceAsync(
            string stripeCustomerId, string currency, int daysUntilDue, IReadOnlyList<InvoiceLineItemView> lineItems,
            string idempotencyKey, CancellationToken cancellationToken = default)
        {
            var id = $"in_fake_{++_invoiceCounter}";
            LastCreatedStripeInvoiceId = id;
            return Task.FromResult(new StripeSentInvoice(id, $"https://stripe.test/invoices/{id}"));
        }

        public Task VoidInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default)
        {
            VoidedInvoiceIds.Add(stripeInvoiceId);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
