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

        // The business-data reads below take no owner: unlike wiki pages, none of these records
        // carry a per-record ACL, and the sentinel:read scope is itself the gate. See the
        // interface for the full reasoning. All are read-only - there is no sentinel:write scope
        // for any of them to pair with.
        api.MapGet("/crm/search", async (string query, IDeveloperApiSentinelReadService service, CancellationToken ct) =>
            Results.Ok(await service.SearchCrmAsync(query, ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.SentinelRead));

        api.MapGet("/crm/pipeline", async (IDeveloperApiSentinelReadService service, CancellationToken ct) =>
            Results.Ok(await service.GetPipelineAsync(ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.SentinelRead));

        api.MapGet("/cms/search", async (string query, IDeveloperApiSentinelReadService service, CancellationToken ct) =>
            Results.Ok(await service.SearchCmsPagesAsync(query, ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.SentinelRead));

        api.MapGet("/health", async (IDeveloperApiSentinelReadService service, CancellationToken ct) =>
            Results.Ok(await service.GetSystemHealthAsync(ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.SentinelRead));

        return app;
    }

    private static string Owner(ClaimsPrincipal user) =>
        user.FindFirstValue(DeveloperApiAuthenticationDefaults.OwnerUsernameClaim) ?? string.Empty;
}
