using Microsoft.AspNetCore.Authorization;

namespace GwsBusinessSuite.Web.Security;

// The global FallbackPolicy (AdminOnly) applies to any endpoint with no [Authorize]/
// [AllowAnonymous] metadata of its own. AddInteractiveServerRenderMode()'s shared circuit/hub
// connection endpoint (/_blazor) carries no per-page metadata - it isn't tied to any one route -
// so without this handler, the fallback silently required the admin role just to open a
// SignalR circuit at all. A non-admin authenticated user (e.g. a client-portal contact) got a
// fully server-rendered page on the initial GET (that request DOES carry its own page's
// [Authorize(Policy=...)] metadata) but the circuit behind it never connected, so every
// @onclick on that page was a silent no-op.
//
// An earlier fix called .AllowAnonymous() on the whole MapRazorComponents<App>() builder chain
// instead of scoping this to just the hub path. That was too broad: it also attached
// [AllowAnonymous] metadata to every individual page's own generated endpoint, which suppresses
// ASP.NET Core's normal EARLY, clean 302-to-login redirect for unauthenticated page requests
// (that redirect happens in AuthorizationMiddleware, before any Razor rendering starts). With
// [AllowAnonymous] present, that early redirect never fires, so the request falls through to
// Blazor's own component-level AuthorizeRouteView/RedirectToLogin instead - and that mechanism
// can only convert to an HTTP redirect if the response hasn't started streaming yet, which
// doesn't reliably hold during static SSR here, producing a blank page instead of a redirect.
// This handler fixes the real problem (only the hub path) without touching page-level
// enforcement at all.
public sealed class BlazorCircuitAuthorizationBypassHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is HttpContext httpContext
            && httpContext.Request.Path.StartsWithSegments("/_blazor"))
        {
            foreach (var requirement in context.PendingRequirements.ToList())
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
