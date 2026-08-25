using System.Security.Claims;
using System.Text.Encodings.Web;
using GwsBusinessSuite.Application.DeveloperApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.Security;

public static class DeveloperApiAuthenticationDefaults
{
    public const string Scheme = "DeveloperApiKey";
    public const string ScopeClaim = "gws:api_scope";
    public const string KeyIdClaim = "gws:api_key_id";
    public const string RateLimitClaim = "gws:api_rate_limit";
    // The admin username that issued this key - lets a scope like sentinel:read enforce
    // per-page ACLs (ISentinelAccessService.CanAccessAsync) as that specific human, rather than
    // under a fixed service identity that would over- or under-grant relative to them.
    public const string OwnerUsernameClaim = "gws:api_key_owner";
}

public static class DeveloperApiPolicies
{
    public static string ForScope(string scope) => $"DeveloperApi:{scope}";
}

public sealed class DeveloperApiAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDeveloperApiKeyService keyService,
    IMemoryCache memoryCache)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var header = values.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var plaintextKey = header["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return AuthenticateResult.Fail("The bearer API key is missing.");
        }

        var failureKey = $"developer-api-auth-fail:{Context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        if (memoryCache.TryGetValue<int>(failureKey, out var failures) && failures >= 30)
        {
            return AuthenticateResult.Fail("Too many invalid API key attempts.");
        }

        var key = await keyService.AuthenticateAsync(plaintextKey, Context.RequestAborted);
        if (key is null)
        {
            memoryCache.Set(failureKey, failures + 1, TimeSpan.FromMinutes(5));
            return AuthenticateResult.Fail("The API key is invalid, expired, or revoked.");
        }
        memoryCache.Remove(failureKey);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new(ClaimTypes.Name, key.Name),
            new(DeveloperApiAuthenticationDefaults.KeyIdClaim, key.Id.ToString()),
            new(DeveloperApiAuthenticationDefaults.RateLimitClaim, key.RateLimitPerMinute.ToString()),
            new(DeveloperApiAuthenticationDefaults.OwnerUsernameClaim, key.CreatedBy)
        };
        claims.AddRange(key.Scopes.Select(scope => new Claim(DeveloperApiAuthenticationDefaults.ScopeClaim, scope)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
