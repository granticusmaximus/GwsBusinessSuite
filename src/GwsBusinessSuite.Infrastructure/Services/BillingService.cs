using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class BillingService(
    IAppDbContext db,
    IStripeInvoicingClient stripe,
    TimeProvider timeProvider,
    // Optional, resolved by DI in production - see AutomationWorkflowService's own comment on
    // this same pattern for why it's nullable (existing tests new this class up directly).
    ISecurityAuditService? securityAudit = null) : IBillingService
{
    // An invoice is a first-class money-movement record, so DueDate is always given to Stripe
    // as "N days from send", not a stored absolute date recomputed at send time - this keeps
    // what Stripe actually sent matching what's shown locally.
    private const int DefaultDaysUntilDue = 14;

    public bool IsStripeConfigured => stripe.IsConfigured;

    public async Task<IReadOnlyList<InvoiceView>> ListInvoicesAsync(CancellationToken cancellationToken = default)
    {
        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column - materialize
        // first, then sort client-side (same fix as everywhere else in this codebase that
        // orders by a DateTimeOffset).
        var invoices = await db.Invoices.AsNoTracking()
            .Include(invoice => invoice.LineItems)
            .ToListAsync(cancellationToken);
        return await ToViewsAsync(invoices.OrderByDescending(invoice => invoice.CreatedAt).ToList(), cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceView>> ListInvoicesForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var invoices = await db.Invoices.AsNoTracking()
            .Include(invoice => invoice.LineItems)
            .Where(invoice => invoice.ContactId == contactId)
            .ToListAsync(cancellationToken);
        return await ToViewsAsync(invoices.OrderByDescending(invoice => invoice.CreatedAt).ToList(), cancellationToken);
    }

    public async Task<InvoiceView?> GetInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken);
        if (invoice is null) return null;

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == invoice.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(invoice, contactName);
    }

    public async Task<InvoiceView> SaveDraftAsync(InvoiceEditorModel editor, string? actor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var title = editor.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Invoice title is required.", nameof(editor));
        }

        var contactExists = await db.Contacts.AsNoTracking()
            .AnyAsync(contact => contact.Id == editor.ContactId && contact.TrashedAt == null, cancellationToken);
        if (!contactExists)
        {
            throw new InvalidOperationException("Select an active contact for this invoice.");
        }

        if (editor.DealId is { } dealId)
        {
            var dealMatchesContact = await db.Deals.AsNoTracking()
                .AnyAsync(deal => deal.Id == dealId && deal.ContactId == editor.ContactId, cancellationToken);
            if (!dealMatchesContact)
            {
                throw new InvalidOperationException("The selected deal does not belong to this invoice's contact.");
            }
        }

        foreach (var lineItem in editor.LineItems)
        {
            if (string.IsNullOrWhiteSpace(lineItem.Description))
            {
                throw new ArgumentException("Every line item needs a description.", nameof(editor));
            }

            if (lineItem.Quantity < 1 || lineItem.UnitPriceUsd <= 0)
            {
                throw new ArgumentException("Line-item quantity and price must be greater than zero.", nameof(editor));
            }
        }

        var now = timeProvider.GetUtcNow();
        Invoice invoice;
        if (editor.Id is Guid id)
        {
            invoice = await db.Invoices.Include(item => item.LineItems)
                .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Invoice {id} was not found.");
            if (invoice.Status != InvoiceStatuses.Draft)
            {
                throw new InvalidOperationException("Only a Draft invoice can be edited.");
            }
            db.InvoiceLineItems.RemoveRange(invoice.LineItems);
            invoice.LineItems.Clear();
        }
        else
        {
            invoice = new Invoice { Title = editor.Title, CreatedAt = now, CreatedBy = actor ?? "system" };
            db.Invoices.Add(invoice);
        }

        invoice.ContactId = editor.ContactId;
        invoice.DealId = editor.DealId;
        invoice.Title = title;
        invoice.DueDate = editor.DueDate is { } dueDate
            ? new DateTimeOffset(dueDate.Date, TimeSpan.Zero)
            : null;
        invoice.Notes = editor.Notes.Trim();
        invoice.UpdatedAt = now;
        invoice.UpdatedBy = actor ?? "system";

        foreach (var lineEditor in editor.LineItems)
        {
            invoice.LineItems.Add(new InvoiceLineItem
            {
                Description = lineEditor.Description.Trim(),
                Quantity = lineEditor.Quantity,
                UnitPriceUsd = lineEditor.UnitPriceUsd,
                CreatedAt = now,
                CreatedBy = actor ?? "system",
                UpdatedAt = now,
                UpdatedBy = actor ?? "system"
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == invoice.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(invoice, contactName);
    }

    public async Task DeleteDraftAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken);
        if (invoice is null) return;
        if (invoice.Status != InvoiceStatuses.Draft)
        {
            throw new InvalidOperationException("Only a Draft invoice can be deleted - void a sent invoice instead.");
        }

        db.InvoiceLineItems.RemoveRange(invoice.LineItems);
        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvoiceView> SendInvoiceAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default)
    {
        if (!IsStripeConfigured)
        {
            throw new InvalidOperationException(
                "Stripe is not configured. Set Stripe:SecretKey and Stripe:PublishableKey before sending invoices.");
        }

        var invoice = await db.Invoices.Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} was not found.");
        if (invoice.Status != InvoiceStatuses.Draft)
        {
            throw new InvalidOperationException("Only a Draft invoice can be sent.");
        }
        if (invoice.LineItems.Count == 0)
        {
            throw new InvalidOperationException("Add at least one line item before sending an invoice.");
        }

        var contact = await db.Contacts.FirstOrDefaultAsync(item => item.Id == invoice.ContactId, cancellationToken)
            ?? throw new InvalidOperationException($"Contact {invoice.ContactId} was not found.");

        var idempotencyKey = $"gws-invoice:{invoice.Id:N}";
        var stripeCustomerId = await stripe.EnsureCustomerAsync(
            contact.StripeCustomerId, contact.FullName, contact.Email, idempotencyKey, cancellationToken);
        if (contact.StripeCustomerId != stripeCustomerId)
        {
            contact.StripeCustomerId = stripeCustomerId;
        }

        var lineItemViews = invoice.LineItems
            .Select(item => new InvoiceLineItemView(item.Id, item.Description, item.Quantity, item.UnitPriceUsd))
            .ToList();
        var now = timeProvider.GetUtcNow();
        var daysUntilDue = invoice.DueDate is { } requestedDueDate
            ? Math.Max(1, (int)Math.Ceiling((requestedDueDate - now).TotalDays))
            : DefaultDaysUntilDue;
        var sent = await stripe.CreateAndSendInvoiceAsync(
            stripeCustomerId, invoice.Currency, daysUntilDue, lineItemViews, idempotencyKey, cancellationToken);

        invoice.Status = InvoiceStatuses.Sent;
        invoice.StripeCustomerId = stripeCustomerId;
        invoice.StripeInvoiceId = sent.StripeInvoiceId;
        invoice.StripeHostedInvoiceUrl = sent.HostedInvoiceUrl;
        invoice.SentAt = now;
        invoice.DueDate = now.AddDays(daysUntilDue);
        invoice.UpdatedAt = now;
        invoice.UpdatedBy = actor ?? "system";
        await db.SaveChangesAsync(cancellationToken);

        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "InvoiceSent", SecurityAuditOutcomes.Succeeded,
                TargetType: "Invoice", TargetId: invoice.Id.ToString(),
                Details: new Dictionary<string, string?>
                {
                    ["contactId"] = invoice.ContactId.ToString(),
                    ["totalUsd"] = lineItemViews.Sum(item => item.TotalUsd).ToString("F2")
                }), cancellationToken);
        }

        return ToView(invoice, contact.FullName);
    }

    public async Task<InvoiceView> VoidInvoiceAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} was not found.");
        if (invoice.Status is not (InvoiceStatuses.Sent))
        {
            throw new InvalidOperationException("Only a Sent invoice can be voided.");
        }

        if (!string.IsNullOrWhiteSpace(invoice.StripeInvoiceId))
        {
            await stripe.VoidInvoiceAsync(invoice.StripeInvoiceId, cancellationToken);
        }

        invoice.Status = InvoiceStatuses.Void;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.UpdatedBy = actor ?? "system";
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == invoice.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(invoice, contactName);
    }

    public async Task MarkInvoicePaidByStripeIdAsync(string stripeInvoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(item => item.StripeInvoiceId == stripeInvoiceId, cancellationToken);
        // Unknown invoice id or already-processed webhook redelivery - both are silent no-ops,
        // since Stripe retries webhook delivery and this must stay idempotent.
        if (invoice is null || invoice.Status == InvoiceStatuses.Paid) return;

        invoice.Status = InvoiceStatuses.Paid;
        invoice.PaidAt = timeProvider.GetUtcNow();
        invoice.UpdatedAt = invoice.PaidAt.Value;
        invoice.UpdatedBy = "stripe-webhook";
        await db.SaveChangesAsync(cancellationToken);

        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "InvoicePaid", SecurityAuditOutcomes.Succeeded,
                TargetType: "Invoice", TargetId: invoice.Id.ToString()), cancellationToken);
        }
    }

    private async Task<IReadOnlyList<InvoiceView>> ToViewsAsync(List<Invoice> invoices, CancellationToken cancellationToken)
    {
        var contactIds = invoices.Select(item => item.ContactId).Distinct().ToList();
        var contactNames = await db.Contacts.AsNoTracking()
            .Where(contact => contactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);

        return invoices
            .Select(invoice => ToView(invoice, contactNames.GetValueOrDefault(invoice.ContactId, "Unknown contact")))
            .ToList();
    }

    private static InvoiceView ToView(Invoice invoice, string contactName) => new(
        invoice.Id,
        invoice.ContactId,
        contactName,
        invoice.DealId,
        invoice.Title,
        invoice.Status,
        invoice.Currency,
        invoice.DueDate,
        invoice.SentAt,
        invoice.PaidAt,
        invoice.Notes,
        invoice.StripeHostedInvoiceUrl,
        invoice.LineItems
            .Select(item => new InvoiceLineItemView(item.Id, item.Description, item.Quantity, item.UnitPriceUsd))
            .ToList(),
        invoice.CreatedAt);
}
