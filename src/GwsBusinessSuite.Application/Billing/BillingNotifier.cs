namespace GwsBusinessSuite.Application.Billing;

/// <summary>
/// App-wide in-memory updates for open Billing lists. The complete row is published so each
/// circuit can update in place without polling or sharing its scoped DbContext across callbacks.
/// </summary>
public sealed class BillingNotifier
{
    public event Action<InvoiceView>? Changed;

    public void Publish(InvoiceView invoice) => Changed?.Invoke(invoice);
}
