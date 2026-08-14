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

// One Google connection is authorized for both Gmail send and Calendar event scopes at once,
// so a single "Connect Google" click produces one credential usable by either node type -
// simpler for the user than two near-identical OAuth flows.
public interface IGoogleOAuthService
{
    bool IsConfigured { get; }
    string CreateAuthorizationUrl(string state);
    Task<AutomationOAuthConnectionResult> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken = default);
}
