namespace GwsBusinessSuite.Application.Mobile;

public static class MobileDevicePlatforms
{
    public const string Ios = "ios";
    public const string Android = "android";

    public static readonly string[] All = [Ios, Android];
}

public sealed record MobileDeviceView(Guid Id, string Platform, string DeviceName, DateTimeOffset RegisteredAt, DateTimeOffset? LastSeenAt);

// A workflow-agnostic summary of one core.approval wait, safe to hand to a mobile client -
// mirrors AutomationPublicStatusView's own "sanitized view" precedent (Part 4.10): no full node
// graph, credentials, or unrelated execution history, just what's needed to show and act on one
// pending approval. NodeName (not the node's configured prompt message) is what's already stored
// directly on AutomationExecution at pause time - showing the full custom message would mean an
// extra published-snapshot fetch per pending approval, a reasonable follow-up, not built here.
public sealed record MobilePendingApprovalView(
    Guid ExecutionId,
    Guid WorkflowId,
    string WorkflowName,
    string NodeName,
    DateTimeOffset? WaitingSince);

public interface IMobilePushRegistrationService
{
    Task<MobileDeviceView> RegisterDeviceAsync(string username, string platform, string pushToken, string deviceName, CancellationToken cancellationToken = default);
    Task UnregisterDeviceAsync(string username, string pushToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MobileDeviceView>> ListDevicesForUserAsync(string username, CancellationToken cancellationToken = default);
}

// Server-side readiness only (Part 4.8) - this is the abstraction a real push implementation
// plugs into once APNs/FCM credentials exist; see NoOpPushNotificationSender for why nothing
// actually sends yet in this environment.
public interface IPushNotificationSender
{
    Task SendAsync(string username, string title, string body, string? deepLinkUrl = null, CancellationToken cancellationToken = default);
}

// Deliberately separate from IAutomationExecutionService (which stays Mobile-agnostic) rather
// than adding a mobile-flavored query there - keeps the dependency direction one-way
// (Mobile depends on Automation's db shape, not the other way around).
public interface IMobileApprovalService
{
    Task<IReadOnlyList<MobilePendingApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default);
}
