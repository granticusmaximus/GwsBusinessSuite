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
    private const int MaxAutoReminders = InvoiceReminders.MaxAutomatic;
    private static readonly TimeSpan AutoReminderGap = TimeSpan.FromDays(7);
    private static readonly TimeSpan ManualReminderGap = TimeSpan.FromHours(20);

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

        var currency = InvoiceCurrencies.Find(editor.Currency)
            ?? throw new ArgumentException($"'{editor.Currency}' isn't a supported invoice currency.", nameof(editor));
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
        invoice.Currency = currency.Code;
        invoice.AutoRemind = editor.AutoRemind;
        invoice.UpdatedAt = now;
        invoice.UpdatedBy = actor ?? "system";

        foreach (var (lineEditor, position) in editor.LineItems.Select((line, index) => (line, index)))
        {
            invoice.LineItems.Add(new InvoiceLineItem
            {
                Position = position,
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
        await ReleaseTimeEntriesAsync(invoice.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvoiceView> SetDraftDueDateAsync(Guid invoiceId, DateTime? dueDate, string? actor = null, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} was not found.");
        if (invoice.Status != InvoiceStatuses.Draft)
        {
            throw new InvalidOperationException("Only a Draft invoice's due date can be changed - Stripe holds a sent invoice's.");
        }

        // Stored as SaveDraftAsync stores it: the date at UTC midnight.
        invoice.DueDate = dueDate is { } date ? new DateTimeOffset(date.Date, TimeSpan.Zero) : null;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.UpdatedBy = actor ?? "system";
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == invoice.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(invoice, contactName);
    }

    public async Task<IReadOnlyList<Guid>> SetAutoRemindAsync(IReadOnlyCollection<Guid> invoiceIds, bool autoRemind, string? actor = null, CancellationToken cancellationToken = default)
    {
        var ids = invoiceIds.Distinct().ToList();
        var invoices = await db.Invoices
            .Where(invoice => ids.Contains(invoice.Id) && invoice.AutoRemind != autoRemind
                && (invoice.Status == InvoiceStatuses.Draft || invoice.Status == InvoiceStatuses.Sent))
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var invoice in invoices)
        {
            invoice.AutoRemind = autoRemind;
            invoice.UpdatedAt = now;
            invoice.UpdatedBy = actor ?? "system";
        }
        await db.SaveChangesAsync(cancellationToken);
        return invoices.Select(invoice => invoice.Id).ToList();
    }

    // Time billed on an invoice that's deleted or voided goes back to unbilled, so it can be billed
    // again (TimeTrackingService.BillUnbilledAsync).
    private async Task ReleaseTimeEntriesAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        foreach (var entry in await db.TimeEntries.Where(t => t.InvoiceId == invoiceId).ToListAsync(cancellationToken))
        {
            entry.InvoiceId = null;
        }
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

        var lineItemViews = Ordered(invoice.LineItems)
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
                    ["total"] = InvoiceCurrencies.Format(lineItemViews.Sum(item => item.TotalUsd), invoice.Currency)
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
        await ReleaseTimeEntriesAsync(invoice.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == invoice.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(invoice, contactName);
    }

    public async Task<InvoiceView> DuplicateAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default)
    {
        var source = await db.Invoices.AsNoTracking().Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} was not found.");
        // Through SaveDraftAsync, so the copy passes the same checks as a hand-made draft (the
        // contact may since have been trashed, for one). Due date isn't copied: it's a new bill.
        return await SaveDraftAsync(new InvoiceEditorModel
        {
            ContactId = source.ContactId,
            DealId = source.DealId,
            Title = source.Title.StartsWith("Copy of ", StringComparison.Ordinal) ? source.Title : $"Copy of {source.Title}",
            Notes = source.Notes,
            Currency = source.Currency,
            AutoRemind = source.AutoRemind,
            LineItems = Ordered(source.LineItems)
                .Select(item => new InvoiceLineItemEditorModel { Description = item.Description, Quantity = item.Quantity, UnitPriceUsd = item.UnitPriceUsd })
                .ToList()
        }, actor, cancellationToken);
    }

    public async Task<InvoiceView> SendReminderAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} was not found.");
        if (invoice.Status != InvoiceStatuses.Sent || string.IsNullOrWhiteSpace(invoice.StripeInvoiceId))
        {
            throw new InvalidOperationException("Only a sent, unpaid invoice can be reminded.");
        }
        var now = timeProvider.GetUtcNow();
        if (invoice.LastReminderAt is { } last && now - last < ManualReminderGap)
        {
            throw new InvalidOperationException("A reminder already went out in the last day.");
        }

        await RemindAsync(invoice, now, actor ?? "system", automatic: false, cancellationToken);
        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == invoice.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(invoice, contactName);
    }

    public async Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStripeConfigured) return 0;
        var now = timeProvider.GetUtcNow();
        // SQLite/EF Core can't translate DateTimeOffset comparisons - filter dates client-side.
        var candidates = (await db.Invoices
                .Where(invoice => invoice.Status == InvoiceStatuses.Sent && invoice.AutoRemind
                    && invoice.StripeInvoiceId != null && invoice.RemindersSent < MaxAutoReminders)
                .ToListAsync(cancellationToken))
            .Where(invoice => invoice.DueDate is { } due && now - due >= TimeSpan.FromDays(1)
                && (invoice.LastReminderAt is not { } last || now - last >= AutoReminderGap))
            .ToList();

        var sent = 0;
        foreach (var invoice in candidates)
        {
            try
            {
                await RemindAsync(invoice, now, "billing-reminders", automatic: true, cancellationToken);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failing invoice (e.g. paid in Stripe moments ago) mustn't stop the rest;
                // it's retried on the next sweep.
                if (securityAudit is not null)
                {
                    await securityAudit.RecordAsync(new SecurityAuditInput(
                        SecurityAuditCategories.DataLifecycle, "InvoiceReminder", SecurityAuditOutcomes.Failed,
                        TargetType: "Invoice", TargetId: invoice.Id.ToString(),
                        Details: new Dictionary<string, string?> { ["error"] = ex.Message }), cancellationToken);
                }
            }
        }
        return sent;
    }

    private async Task RemindAsync(Invoice invoice, DateTimeOffset now, string actor, bool automatic, CancellationToken cancellationToken)
    {
        await stripe.ResendInvoiceAsync(invoice.StripeInvoiceId!, cancellationToken);
        invoice.RemindersSent++;
        invoice.LastReminderAt = now;
        invoice.UpdatedAt = now;
        invoice.UpdatedBy = actor;
        await db.SaveChangesAsync(cancellationToken);

        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "InvoiceReminder", SecurityAuditOutcomes.Succeeded,
                TargetType: "Invoice", TargetId: invoice.Id.ToString(),
                Details: new Dictionary<string, string?>
                {
                    ["automatic"] = automatic ? "true" : "false",
                    ["count"] = invoice.RemindersSent.ToString()
                }), cancellationToken);
        }
    }

    public async Task MarkInvoicePaidByStripeIdAsync(string stripeInvoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(item => item.StripeInvoiceId == stripeInvoiceId, cancellationToken);
        // Unknown invoice id or already-processed webhook redelivery - both are silent no-ops,
        // since Stripe retries webhook delivery and this must stay idempotent. Void is included
        // deliberately, not just Paid: Stripe doesn't guarantee ordered/immediate delivery, so a
        // delayed or redelivered invoice.paid event arriving after an admin voids the invoice
        // must never flip a deliberately-voided invoice back to Paid.
        if (invoice is null || invoice.Status is InvoiceStatuses.Paid or InvoiceStatuses.Void) return;

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

    // Lines in the order they were entered (Id breaks ties for lines saved before Position existed).
    private static IEnumerable<InvoiceLineItem> Ordered(IEnumerable<InvoiceLineItem> lines) =>
        lines.OrderBy(item => item.Position).ThenBy(item => item.Id);

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
        Ordered(invoice.LineItems)
            .Select(item => new InvoiceLineItemView(item.Id, item.Description, item.Quantity, item.UnitPriceUsd))
            .ToList(),
        invoice.CreatedAt,
        invoice.AutoRemind,
        invoice.RemindersSent,
        invoice.LastReminderAt);
}
