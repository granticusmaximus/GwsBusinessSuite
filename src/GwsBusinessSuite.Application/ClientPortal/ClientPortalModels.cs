namespace GwsBusinessSuite.Application.ClientPortal;

// The identity a successful client-portal sign-in resolves to - deliberately narrow (just what's
// needed to render "you're signed in as X" and scope subsequent queries), mirroring the
// "sanitized view" precedent already established for other external-facing surfaces
// (AutomationPublicStatusView, MobilePendingApprovalView).
public sealed record ClientPortalContactView(Guid ContactId, string FullName, string Email);

public interface IClientPortalAuthService
{
    // Always completes the same way regardless of whether email matches a real contact - never
    // reveals which addresses are registered. Silently no-ops (no token minted, no email sent)
    // on a miss. loginBaseUrl is the caller's own page URL (e.g. NavigationManager.BaseUri +
    // "client-portal/login") so the emailed link always points at whatever host actually served
    // the request, rather than a hardcoded domain baked into config.
    Task RequestLoginLinkAsync(string email, string loginBaseUrl, string? requestedFromIp = null, CancellationToken cancellationToken = default);

    // Null for an invalid, expired, or already-consumed token - the caller shows one generic
    // "this link is no longer valid" message either way, never distinguishing why.
    Task<ClientPortalContactView?> ConsumeLoginLinkAsync(string token, CancellationToken cancellationToken = default);
}

public interface IClientPortalEmailSender
{
    Task SendLoginLinkAsync(string toEmail, string contactName, string loginUrl, CancellationToken cancellationToken = default);
}
