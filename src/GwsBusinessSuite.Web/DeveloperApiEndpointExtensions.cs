using System.Security.Claims;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Web.Security;

namespace GwsBusinessSuite.Web;

public static class DeveloperApiEndpointExtensions
{
    public static WebApplication MapDeveloperApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .WithTags("Developer API")
            .RequireRateLimiting("developer-api");

        api.MapGet("/contacts", async (int? page, int? pageSize, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.ListContactsAsync(page ?? 1, pageSize ?? 50, ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.ContactsRead));
        api.MapGet("/contacts/{id:guid}", async (Guid id, IDeveloperApiResourceService service, CancellationToken ct) =>
            (await service.GetContactAsync(id, ct)) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.ContactsRead));
        api.MapPost("/contacts", async (DeveloperApiContactInput input, ClaimsPrincipal user, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteCreatedAsync(() => service.CreateContactAsync(input, Actor(user), ct), "contacts"))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.ContactsWrite));
        api.MapPut("/contacts/{id:guid}", async (Guid id, DeveloperApiContactInput input, ClaimsPrincipal user, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteUpdateAsync(() => service.UpdateContactAsync(id, input, Actor(user), ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.ContactsWrite));

        api.MapGet("/deals", async (int? page, int? pageSize, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.ListDealsAsync(page ?? 1, pageSize ?? 50, ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.DealsRead));
        api.MapGet("/deals/{id:guid}", async (Guid id, IDeveloperApiResourceService service, CancellationToken ct) =>
            (await service.GetDealAsync(id, ct)) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.DealsRead));
        api.MapPost("/deals", async (DeveloperApiDealInput input, ClaimsPrincipal user, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteCreatedAsync(() => service.CreateDealAsync(input, Actor(user), ct), "deals"))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.DealsWrite));
        api.MapPut("/deals/{id:guid}", async (Guid id, DeveloperApiDealInput input, ClaimsPrincipal user, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteUpdateAsync(() => service.UpdateDealAsync(id, input, Actor(user), ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.DealsWrite));

        api.MapGet("/cms-pages", async (int? page, int? pageSize, Guid? siteId, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.ListCmsPagesAsync(page ?? 1, pageSize ?? 50, siteId, ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.CmsPagesRead));
        api.MapGet("/cms-pages/{id:guid}", async (Guid id, IDeveloperApiResourceService service, CancellationToken ct) =>
            (await service.GetCmsPageAsync(id, ct)) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.CmsPagesRead));
        api.MapPost("/cms-pages", async (DeveloperApiCmsPageInput input, ClaimsPrincipal user, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteCreatedAsync(() => service.CreateCmsPageAsync(input, Actor(user), ct), "cms-pages"))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.CmsPagesWrite));
        api.MapPut("/cms-pages/{id:guid}", async (Guid id, DeveloperApiCmsPageInput input, ClaimsPrincipal user, IDeveloperApiResourceService service, CancellationToken ct) =>
            await ExecuteUpdateAsync(() => service.UpdateCmsPageAsync(id, input, Actor(user), ct)))
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.CmsPagesWrite));

        return app;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (InvalidOperationException ex) { return ValidationProblem(ex.Message); }
        catch (ArgumentException ex) { return ValidationProblem(ex.Message); }
    }

    private static async Task<IResult> ExecuteCreatedAsync<T>(Func<Task<T>> operation, string collection)
        where T : IDeveloperApiResource
    {
        try
        {
            var item = await operation();
            return Results.Created($"/api/v1/{collection}/{item.Id}", item);
        }
        catch (InvalidOperationException ex) { return ValidationProblem(ex.Message); }
        catch (ArgumentException ex) { return ValidationProblem(ex.Message); }
    }

    private static async Task<IResult> ExecuteUpdateAsync<T>(Func<Task<T?>> operation) where T : class
    {
        try { return await operation() is { } item ? Results.Ok(item) : Results.NotFound(); }
        catch (InvalidOperationException ex) { return ValidationProblem(ex.Message); }
        catch (ArgumentException ex) { return ValidationProblem(ex.Message); }
    }

    private static IResult ValidationProblem(string detail) => Results.Problem(
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest,
        title: "Request validation failed");

    private static string Actor(ClaimsPrincipal user) =>
        $"api:{user.FindFirstValue(DeveloperApiAuthenticationDefaults.KeyIdClaim) ?? "unknown"}";
}
