using GwsBusinessSuite.Application.Wiki;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Web;

// Public Sentinel shares (SentinelPublicShare.razor) render as a static page, so the password
// gate posts here - the same static-page + endpoint shape as /auth/login. A correct password sets
// a signed, share-scoped cookie that SentinelShareUnlock checks on every page of the share, so a
// visitor of a published site unlocks it once rather than on each page.
public static class SentinelShareEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/sentinel/share/{token}/unlock", UnlockAsync)
            .AllowAnonymous().RequireRateLimiting("login");
        app.MapPost("/sentinel/form/{token}/submit", SubmitFormAsync)
            .AllowAnonymous().RequireRateLimiting("public-write");
    }

    // A public form (SentinelPublicForm.razor) response becomes a row. Antiforgery, a honeypot
    // field and Turnstile (when configured) come first; SentinelPublicFormService then checks the
    // answers themselves.
    private static async Task<IResult> SubmitFormAsync(
        string token,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        TurnstileService turnstile,
        ISentinelPublicFormService forms)
    {
        var back = $"/sentinel/form/{token}";
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.LocalRedirect(back + "?error=expired");
        }
        var form = await httpContext.Request.ReadFormAsync();
        // Only a bot fills in the field people can't see; it's told it worked.
        if (!string.IsNullOrEmpty(form["website"])) return Results.LocalRedirect(back + "?sent=true");
        if (turnstile.IsConfigured)
        {
            var verification = await turnstile.VerifyAsync(form[TurnstileService.ResponseField].ToString(), httpContext.Request.Host.Host,
                httpContext.Connection.RemoteIpAddress?.ToString(), httpContext.RequestAborted, TurnstileService.FormAction);
            if (verification != TurnstileResult.Verified) return Results.LocalRedirect(back + "?error=verification");
        }
        var answers = form
            .Where(field => Guid.TryParse(field.Key, out _))
            .ToDictionary(field => field.Key, field => (IReadOnlyList<string>)field.Value.Select(value => value ?? string.Empty).ToList());
        var result = await forms.SubmitAsync(token, answers, httpContext.RequestAborted);
        if (result.Saved) return Results.LocalRedirect(back + "?sent=true");
        var message = string.Join(" ", result.Errors);
        return Results.LocalRedirect(back + "?error=" + Uri.EscapeDataString(message.Length > 500 ? message[..500] : message));
    }

    private static async Task<IResult> UnlockAsync(
        string token,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ISentinelAccessService accessService,
        IDataProtectionProvider dataProtection)
    {
        var form = await httpContext.Request.ReadFormAsync();
        var back = Guid.TryParse(form["page"], out var pageId) ? $"/sentinel/share/{token}/{pageId}" : $"/sentinel/share/{token}";
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.LocalRedirect(back + "?unlock=expired");
        }
        var share = await accessService.ResolvePublicShareAsync(token);
        if (share is null || !share.RequiresPassword) return Results.LocalRedirect(back);
        if (!await accessService.VerifySharePasswordAsync(share.Id, form["password"].ToString()))
            return Results.LocalRedirect(back + "?unlock=wrong");
        SentinelShareUnlock.Grant(httpContext, dataProtection, token, share.Id);
        return Results.LocalRedirect(back);
    }
}

public static class SentinelShareUnlock
{
    private const string Purpose = "SentinelPublicShareUnlock";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static string CookieName(Guid shareId) => $"sentinel_share_{shareId:N}";

    public static void Grant(HttpContext httpContext, IDataProtectionProvider dataProtection, string token, Guid shareId)
    {
        var value = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Protect(shareId.ToString("N"), Lifetime);
        httpContext.Response.Cookies.Append(CookieName(shareId), value, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = $"/sentinel/share/{token}",
            MaxAge = Lifetime,
            IsEssential = true
        });
    }

    public static bool IsUnlocked(HttpContext? httpContext, IDataProtectionProvider dataProtection, Guid shareId)
    {
        if (httpContext?.Request.Cookies.TryGetValue(CookieName(shareId), out var value) != true || string.IsNullOrEmpty(value)) return false;
        try
        {
            return dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(value) == shareId.ToString("N");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}
