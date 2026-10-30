namespace GwsBusinessSuite.Application.Abstractions;

// Who the work in a DI scope is done for, when no request or circuit says so. A SentinelGPT agent
// turn (30-10-26) resolves its tools in a fresh scope, so without this the services that record
// "who" through ICurrentUserAccessor (CrmService, the security audit) would write "unknown".
// The agent runner sets it; CurrentUserAccessor reads it first.
public sealed class ActingUser
{
    public string? Username { get; set; }
}
