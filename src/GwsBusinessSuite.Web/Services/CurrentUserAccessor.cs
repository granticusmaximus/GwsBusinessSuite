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

        var authState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var circuitUsername = authState.User.Identity?.IsAuthenticated == true
            ? authState.User.Identity?.Name
            : null;

        return string.IsNullOrWhiteSpace(circuitUsername) ? "unknown" : circuitUsername;
    }
}
