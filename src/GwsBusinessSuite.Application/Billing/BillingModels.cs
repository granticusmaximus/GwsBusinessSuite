using System.ComponentModel.DataAnnotations;
using System.Globalization;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Billing;

// The currencies an invoice can be priced in, with how many decimal places each uses. Stripe takes
// amounts in the smallest unit (cents for USD, whole yen for JPY), so this table decides the
// multiplier. Unit prices keep their historical *Usd names but are in the invoice's currency.
public static class InvoiceCurrencies
{
    public sealed record Currency(string Code, string Name, int Decimals);

    public static readonly IReadOnlyList<Currency> All =
    [
        new("usd", "US dollar", 2), new("eur", "Euro", 2), new("gbp", "British pound", 2),
        new("cad", "Canadian dollar", 2), new("aud", "Australian dollar", 2), new("nzd", "New Zealand dollar", 2),
        new("chf", "Swiss franc", 2), new("sek", "Swedish krona", 2), new("nok", "Norwegian krone", 2),
        new("dkk", "Danish krone", 2), new("mxn", "Mexican peso", 2), new("brl", "Brazilian real", 2),
        new("inr", "Indian rupee", 2), new("sgd", "Singapore dollar", 2), new("hkd", "Hong Kong dollar", 2),
        new("zar", "South African rand", 2), new("jpy", "Japanese yen", 0), new("krw", "South Korean won", 0)
    ];

    public static Currency? Find(string? code) =>
        All.FirstOrDefault(currency => string.Equals(currency.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    // The amount in Stripe's smallest unit (e.g. 12.34 USD -> 1234, 1234 JPY -> 1234).
    public static long ToMinorUnits(decimal amount, string code)
    {
        var decimals = Find(code)?.Decimals ?? 2;
        return checked((long)decimal.Round(amount * (decimal)Math.Pow(10, decimals), 0, MidpointRounding.AwayFromZero));
    }

    // "USD 1,234.50" / "JPY 1,234" - the code, not a symbol, so $ never means the wrong dollar.
    public static string Format(decimal amount, string code)
    {
        var decimals = Find(code)?.Decimals ?? 2;
        return $"{code.ToUpperInvariant()} {amount.ToString("N" + decimals, CultureInfo.InvariantCulture)}";
    }
}

public static class InvoiceReminders
{
    // Automatic reminders per invoice: a day after the due date, then weekly, this many at most.
    public const int MaxAutomatic = 3;
}

public sealed record InvoiceLineItemView(Guid Id, string Description, int Quantity, decimal UnitPriceUsd)
{
    public decimal TotalUsd => Quantity * UnitPriceUsd;
}

public sealed record InvoiceView(
    Guid Id,
    Guid ContactId,
    string ContactName,
    Guid? DealId,
    string Title,
    string Status,
    string Currency,
    DateTimeOffset? DueDate,
    DateTimeOffset? SentAt,
    DateTimeOffset? PaidAt,
    string Notes,
    string? StripeHostedInvoiceUrl,
    IReadOnlyList<InvoiceLineItemView> LineItems,
    DateTimeOffset CreatedAt,
    bool AutoRemind = false,
    int RemindersSent = 0,
    DateTimeOffset? LastReminderAt = null)
{
    // In Currency (see InvoiceCurrencies).
    public decimal TotalUsd => LineItems.Sum(item => item.TotalUsd);

    public string TotalText => InvoiceCurrencies.Format(TotalUsd, Currency);

    public bool IsOverdue(DateTimeOffset now) => Status == InvoiceStatuses.Sent && DueDate is { } due && due < now;
}

public sealed class InvoiceLineItemEditorModel
{
    public Guid? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal UnitPriceUsd { get; set; }
}

public sealed class InvoiceEditorModel
{
    public Guid? Id { get; set; }
    public Guid ContactId { get; set; }
    public Guid? DealId { get; set; }
    [Required]
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset? DueDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string Currency { get; set; } = "usd";
    // Remind the client automatically once the invoice is overdue (see BillingService).
    public bool AutoRemind { get; set; }
    public List<InvoiceLineItemEditorModel> LineItems { get; set; } = [];
}

public interface IBillingService
{
    // False until Stripe:SecretKey (and PublishableKey) are configured - SendInvoiceAsync
    // throws InvalidOperationException while this is false, same "ask before it breaks
    // mid-action" contract as INotionOAuthService.IsConfigured.
    bool IsStripeConfigured { get; }

    Task<IReadOnlyList<InvoiceView>> ListInvoicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvoiceView>> ListInvoicesForContactAsync(Guid contactId, CancellationToken cancellationToken = default);
    Task<InvoiceView?> GetInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    // Create/update a Draft only - never touches Stripe. The only way to reach Stripe is
    // SendInvoiceAsync.
    Task<InvoiceView> SaveDraftAsync(InvoiceEditorModel editor, string? actor = null, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);

    // List edits (tech-forward T3). A due date changes only on a Draft: once sent, Stripe has it.
    Task<InvoiceView> SetDraftDueDateAsync(Guid invoiceId, DateTime? dueDate, string? actor = null, CancellationToken cancellationToken = default);

    // Turns automatic reminders on or off for the Draft and Sent invoices listed (Paid and Void
    // ones have nothing to remind about) and returns the ids it changed, so Undo flips just those.
    Task<IReadOnlyList<Guid>> SetAutoRemindAsync(IReadOnlyCollection<Guid> invoiceIds, bool autoRemind, string? actor = null, CancellationToken cancellationToken = default);

    // Lazily creates (and reuses) a Stripe Customer for the invoice's contact, then creates and
    // sends a Stripe-hosted invoice for the current line items. Throws InvalidOperationException
    // if IsStripeConfigured is false, or if the invoice has no line items.
    Task<InvoiceView> SendInvoiceAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);
    Task<InvoiceView> VoidInvoiceAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);

    // A new Draft with the same contact, deal, currency, notes and line items - works on any
    // invoice, including Paid and Void ones.
    Task<InvoiceView> DuplicateAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);

    // Re-sends a Sent invoice's Stripe email to the client. At most one reminder per day.
    Task<InvoiceView> SendReminderAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);

    // The background sweep: reminds overdue AutoRemind invoices (first a day after the due date,
    // then weekly, at most MaxAutoReminders). Returns how many were sent.
    Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default);

    // Invoked by the /webhooks/stripe endpoint once a signature-verified "invoice.paid" event
    // arrives - a no-op if the invoice is already marked Paid (webhooks can redeliver).
    Task MarkInvoicePaidByStripeIdAsync(string stripeInvoiceId, CancellationToken cancellationToken = default);
}

// Thin seam over the real Stripe API so BillingService's logic (draft composition, status
// transitions, line-item totals) can be unit tested without a network call or a live API key -
// StripeInvoicingClient is the only thing that actually talks to Stripe.
public interface IStripeInvoicingClient
{
    bool IsConfigured { get; }

    Task<string> EnsureCustomerAsync(
        string? existingStripeCustomerId,
        string contactName,
        string? contactEmail,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<StripeSentInvoice> CreateAndSendInvoiceAsync(
        string stripeCustomerId,
        string currency,
        int daysUntilDue,
        IReadOnlyList<InvoiceLineItemView> lineItems,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task VoidInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default);

    // Stripe emails the open invoice to the customer again (a payment reminder).
    Task ResendInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default);
}

public sealed record StripeSentInvoice(string StripeInvoiceId, string HostedInvoiceUrl);
