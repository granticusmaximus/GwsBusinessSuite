using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Billing;

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
    DateTimeOffset CreatedAt)
{
    public decimal TotalUsd => LineItems.Sum(item => item.TotalUsd);
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

    // Lazily creates (and reuses) a Stripe Customer for the invoice's contact, then creates and
    // sends a Stripe-hosted invoice for the current line items. Throws InvalidOperationException
    // if IsStripeConfigured is false, or if the invoice has no line items.
    Task<InvoiceView> SendInvoiceAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);
    Task<InvoiceView> VoidInvoiceAsync(Guid invoiceId, string? actor = null, CancellationToken cancellationToken = default);

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
}

public sealed record StripeSentInvoice(string StripeInvoiceId, string HostedInvoiceUrl);
