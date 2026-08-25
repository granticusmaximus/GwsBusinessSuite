using System.Security.Claims;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Web.Security;

namespace GwsBusinessSuite.Web;

public static class DeveloperApiSentinelEndpointExtensions
{
    public static WebApplication MapDeveloperApiSentinelEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/sentinel")
            .WithTags("Developer API")
            .RequireRateLimiting("developer-api");

        api.MapGet("/search", async (string query, ClaimsPrincipal user, IDeveloperApiSentinelReadService service, CancellationToken ct) =>
            Results.Ok(await service.SearchWikiAsync(query, Owner(user), ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.SentinelRead));

        api.MapGet("/pages/{id:guid}", async (Guid id, ClaimsPrincipal user, IDeveloperApiSentinelReadService service, CancellationToken ct) =>
            await service.GetPageAsync(id, Owner(user), ct) is { } page ? Results.Ok(page) : Results.NotFound())
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.SentinelRead));

        return app;
    }

    private static string Owner(ClaimsPrincipal user) =>
        user.FindFirstValue(DeveloperApiAuthenticationDefaults.OwnerUsernameClaim) ?? string.Empty;
}
