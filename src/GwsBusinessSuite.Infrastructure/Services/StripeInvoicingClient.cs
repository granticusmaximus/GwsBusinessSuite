using GwsBusinessSuite.Application.Billing;
using Microsoft.Extensions.Options;
using Stripe;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class StripeBillingOptions
{
    public const string SectionName = "Stripe";

    public string SecretKey { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}

public sealed class StripeInvoicingClient(IOptions<StripeBillingOptions> configuredOptions) : IStripeInvoicingClient
{
    private readonly StripeBillingOptions _options = configuredOptions.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.SecretKey)
        && !string.IsNullOrWhiteSpace(_options.PublishableKey);

    public async Task<string> EnsureCustomerAsync(
        string? existingStripeCustomerId,
        string contactName,
        string? contactEmail,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(existingStripeCustomerId))
        {
            return existingStripeCustomerId;
        }

        var customer = await CreateClient().V1.Customers.CreateAsync(new CustomerCreateOptions
        {
            Name = contactName,
            Email = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail
        }, new RequestOptions { IdempotencyKey = $"{idempotencyKey}:customer" }, cancellationToken);
        return customer.Id;
    }

    public async Task<StripeSentInvoice> CreateAndSendInvoiceAsync(
        string stripeCustomerId,
        string currency,
        int daysUntilDue,
        IReadOnlyList<InvoiceLineItemView> lineItems,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var invoice = await client.V1.Invoices.CreateAsync(new InvoiceCreateOptions
        {
            Customer = stripeCustomerId,
            Currency = currency,
            CollectionMethod = "send_invoice",
            DaysUntilDue = daysUntilDue,
            AutoAdvance = false
        }, new RequestOptions { IdempotencyKey = $"{idempotencyKey}:invoice" }, cancellationToken);

        try
        {
            for (var index = 0; index < lineItems.Count; index++)
            {
                var lineItem = lineItems[index];
                await client.V1.InvoiceItems.CreateAsync(new InvoiceItemCreateOptions
                {
                    Customer = stripeCustomerId,
                    Invoice = invoice.Id,
                    Currency = currency,
                    Amount = checked((long)decimal.Round(lineItem.TotalUsd * 100m, 0, MidpointRounding.AwayFromZero)),
                    Description = lineItem.Quantity == 1
                        ? lineItem.Description
                        : $"{lineItem.Description} ({lineItem.Quantity} x {lineItem.UnitPriceUsd:C2})"
                }, new RequestOptions { IdempotencyKey = $"{idempotencyKey}:line:{index}" }, cancellationToken);
            }

            invoice = await client.V1.Invoices.FinalizeInvoiceAsync(
                invoice.Id, new InvoiceFinalizeOptions(),
                new RequestOptions { IdempotencyKey = $"{idempotencyKey}:finalize" }, cancellationToken);
            invoice = await client.V1.Invoices.SendInvoiceAsync(
                invoice.Id, new InvoiceSendOptions(),
                new RequestOptions { IdempotencyKey = $"{idempotencyKey}:send" }, cancellationToken);
            return new StripeSentInvoice(invoice.Id, invoice.HostedInvoiceUrl ?? string.Empty);
        }
        catch
        {
            // Keep a failed multi-call send from leaving an actionable draft in Stripe.
            // Deleting is valid only while the invoice is still a draft; if finalization
            // already happened Stripe rejects this best-effort cleanup and the original
            // exception remains the one surfaced to the caller.
            try
            {
                await client.V1.Invoices.DeleteAsync(invoice.Id, cancellationToken: cancellationToken);
            }
            catch (StripeException)
            {
                // Best-effort cleanup only.
            }

            throw;
        }
    }

    public async Task VoidInvoiceAsync(string stripeInvoiceId, CancellationToken cancellationToken = default)
    {
        await CreateClient().V1.Invoices.VoidInvoiceAsync(
            stripeInvoiceId, new InvoiceVoidOptions(), cancellationToken: cancellationToken);
    }

    private StripeClient CreateClient()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Stripe is not configured. Set Stripe:SecretKey and Stripe:PublishableKey.");
        }

        return new StripeClient(_options.SecretKey);
    }
}
