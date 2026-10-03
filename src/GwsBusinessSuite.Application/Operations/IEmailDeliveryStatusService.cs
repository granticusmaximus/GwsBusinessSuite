namespace GwsBusinessSuite.Application.Operations;

public sealed record EmailDeliveryFeatureStatus(string Feature, bool CanSend, string Route, string FromAddress);

public sealed record EmailDeliveryTestResult(bool Succeeded, string Message);

// Which route (SMTP, local pickup directory, or the connected Google account) each email
// feature will actually send through, plus a one-click test send - shown on Settings.
public interface IEmailDeliveryStatusService
{
    Task<IReadOnlyList<EmailDeliveryFeatureStatus>> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<EmailDeliveryTestResult> SendTestAsync(string toAddress, CancellationToken cancellationToken = default);
}
