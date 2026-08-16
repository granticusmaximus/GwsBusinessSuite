using GwsBusinessSuite.Application.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;

namespace GwsBusinessSuite.Web.Services;

public sealed class CurrentUserAccessor(
    IHttpContextAccessor httpContextAccessor,
    AuthenticationStateProvider authenticationStateProvider) : ICurrentUserAccessor
{
    public async Task<string> GetCurrentUsernameAsync(CancellationToken cancellationToken = default)
    {
        var httpUsername = httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true
            ? httpContextAccessor.HttpContext.User.Identity?.Name
            : null;

        if (!string.IsNullOrWhiteSpace(httpUsername))
        {
            return httpUsername;
        }

        // ServerAuthenticationStateProvider.GetAuthenticationStateAsync throws
        // InvalidOperationException when there's no active Razor Components circuit to read -
        // true for any startup-time or background-service caller (e.g. Program.cs's seed
        // steps, which run before any user ever connects). No HttpContext AND no circuit both
        // mean "no known caller," same as the existing "unknown" fallback below already covers.
        AuthenticationState authState;
        try
        {
            authState = await authenticationStateProvider.GetAuthenticationStateAsync();
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }

        var circuitUsername = authState.User.Identity?.IsAuthenticated == true
            ? authState.User.Identity?.Name
            : null;

        return string.IsNullOrWhiteSpace(circuitUsername) ? "unknown" : circuitUsername;
    }
}
