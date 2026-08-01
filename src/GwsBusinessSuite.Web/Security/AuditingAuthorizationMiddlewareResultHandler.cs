using GwsBusinessSuite.Application.SecurityAudit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace GwsBusinessSuite.Web.Security;

public sealed class AuditingAuthorizationMiddlewareResultHandler(
    ILogger<AuditingAuthorizationMiddlewareResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if ((authorizeResult.Forbidden || authorizeResult.Challenged) && ShouldAudit(context.Request.Path))
        {
            try
            {
                var audit = context.RequestServices.GetRequiredService<ISecurityAuditService>();
                await audit.RecordAsync(new SecurityAuditInput(
                    SecurityAuditCategories.Authorization,
                    authorizeResult.Forbidden ? "RequestForbidden" : "AuthenticationRequired",
                    SecurityAuditOutcomes.Denied,
                    SecurityAuditSeverities.Warning,
                    "RequestPath",
                    context.Request.Path.Value,
                    new Dictionary<string, string?>
                    {
                        ["method"] = context.Request.Method,
                        ["authenticated"] = (context.User.Identity?.IsAuthenticated == true).ToString()
                    },
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.User.Identity?.Name ?? "anonymous"),
                    context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to record a denied authorization request for {Path}.", context.Request.Path);
            }
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool ShouldAudit(PathString path)
    {
        if (path.Equals("/Error", StringComparison.OrdinalIgnoreCase)
            || Path.HasExtension(path.Value ?? string.Empty))
        {
            return false;
        }

        return path.StartsWithSegments("/admin")
               || path.StartsWithSegments("/auth")
               || path.StartsWithSegments("/api");
    }
}
