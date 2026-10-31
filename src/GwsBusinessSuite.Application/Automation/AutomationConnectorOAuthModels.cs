namespace GwsBusinessSuite.Application.Automation;

public sealed record AutomationOAuthConnectionResult(bool IsSuccess, string Message);

// Slack/Google connector "connect" flows, mirroring GwsBusinessSuite.Application.Wiki's
// INotionOAuthService shape - CompleteAuthorizationAsync stores the resulting tokens as a
// regular AutomationCredential (TypeKey "oauth2"), reusing IAutomationCredentialService's
// already-generic OAuth2 storage/refresh system rather than inventing per-provider
// persistence. Node executors (slack.sendMessage, gmail.sendEmail, calendar.createEvent)
// then just pick that credential like any other.
public interface ISlackOAuthService
{
    bool IsConfigured { get; }
    string CreateAuthorizationUrl(string state);
    Task<AutomationOAuthConnectionResult> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken = default);
}

// A person's own Gmail connected for Mail (31-10-26): the credential holding its tokens and the
// address Google signed in as.
public sealed record GoogleMailConnectionResult(bool IsSuccess, string Message, Guid? CredentialId = null, string? Address = null);

// One Google connection is authorized for Gmail (read + send, since 31-10-26) and Calendar event
// scopes at once, so a single "Connect Google" click produces one credential usable by every node
// type and by mail-in - simpler for the user than several near-identical OAuth flows.
public interface IGoogleOAuthService
{
    bool IsConfigured { get; }
    string CreateAuthorizationUrl(string state);
    Task<AutomationOAuthConnectionResult> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken = default);
    // Mail (/admin/mail): the same consent, saved as that person's own credential (credentialName).
    Task<GoogleMailConnectionResult> CompleteMailAuthorizationAsync(string code, string credentialName, CancellationToken cancellationToken = default);
}
