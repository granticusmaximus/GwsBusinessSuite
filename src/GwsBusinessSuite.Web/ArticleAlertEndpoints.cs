using System.Net;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Web;

// Public endpoints behind the "Email signup" page widget and the article-alert emails:
// subscribe (double opt-in), confirm, and unsubscribe - including RFC 8058 one-click POSTs that
// mail providers send on the subscriber's behalf.
public static class ArticleAlertEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/subscribe", SubscribeAsync)
            .AllowAnonymous().DisableAntiforgery().RequireRateLimiting("public-write");

        // The widget asks for the Turnstile site key at runtime instead of every page render
        // threading it through CmsBlockHtmlRenderer.
        app.MapGet("/campaigns/signup-config", (TurnstileService turnstile, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { turnstileSiteKey = turnstile.SiteKey });
        }).AllowAnonymous().RequireRateLimiting("public-read");

        app.MapGet("/campaigns/confirm/{token}", ConfirmAsync)
            .AllowAnonymous().RequireRateLimiting("public-read");

        // GET only shows a confirm button - link scanners (e.g. corporate "safe links") prefetch
        // GETs, and must not unsubscribe anyone. The button and one-click clients POST.
        app.MapGet("/campaigns/alerts/unsubscribe/{token}", (string token, HttpContext context, ICmsBuilderService cms, IConfiguration configuration) =>
            PageAsync(context, cms, configuration, "Unsubscribe", "Stop new-article emails?",
                $"""<p>You'll stop receiving emails about new articles. You can sign up again any time.</p><form method="post" action="/campaigns/alerts/unsubscribe/{WebUtility.HtmlEncode(token)}"><button type="submit" class="btn btn-primary">Unsubscribe</button></form>"""))
            .AllowAnonymous().RequireRateLimiting("public-read");

        app.MapPost("/campaigns/alerts/unsubscribe/{token}", UnsubscribeAsync)
            .AllowAnonymous().DisableAntiforgery().RequireRateLimiting("public-write");
    }

    private static async Task<IResult> SubscribeAsync(
        Guid campaignId, HttpRequest request, HttpContext context, IArticleAlertService alerts,
        TurnstileService turnstile, ICmsBuilderService cms, IConfiguration configuration)
    {
        if (!request.HasFormContentType) return Results.BadRequest();
        var form = await request.ReadFormAsync(context.RequestAborted);
        var wantsJson = request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);

        IResult Respond(int status, string message) => wantsJson
            ? Results.Json(status == StatusCodes.Status200OK ? new { ok = true, message } : (object)new { ok = false, error = message }, statusCode: status)
            : PageResult(context, cms, configuration, status == StatusCodes.Status200OK ? "Check your inbox" : "Couldn't sign you up",
                status == StatusCodes.Status200OK ? "Check your inbox" : "Couldn't sign you up", $"<p>{WebUtility.HtmlEncode(message)}</p>", status);

        const string checkInbox = "Almost done - check your inbox and click the link to confirm your subscription.";

        // Honeypot filled = a bot. Answer exactly like a success so it learns nothing.
        if (!string.IsNullOrWhiteSpace(form["_hp"])) return Respond(StatusCodes.Status200OK, checkInbox);

        // Double opt-in is the main protection (nobody receives alerts without clicking the link
        // sent to that inbox); Turnstile is enforced on top whenever it's configured.
        if (turnstile.IsConfigured)
        {
            var verification = await turnstile.VerifyAsync(form[TurnstileService.ResponseField].ToString(), request.Host.Host,
                context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted, TurnstileService.SubscribeAction);
            if (verification != TurnstileResult.Verified)
            {
                return Respond(verification == TurnstileResult.Unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status400BadRequest,
                    verification == TurnstileResult.Unavailable
                        ? "Verification is temporarily unavailable. Please try again shortly."
                        : "Please complete the verification, then try again.");
            }
        }

        var outcome = await alerts.SubscribeAsync(campaignId, new ArticleAlertSignupRequest
        {
            Email = form["email"].ToString(),
            FirstName = form["firstName"].ToString(),
            SourcePath = form["_path"].ToString(),
            ConsentText = form["_consent"].ToString()
        }, context.RequestAborted);

        return outcome switch
        {
            ArticleAlertSignupOutcome.InvalidEmail => Respond(StatusCodes.Status400BadRequest, "Please enter a valid email address."),
            ArticleAlertSignupOutcome.CampaignUnavailable => Respond(StatusCodes.Status404NotFound, "This signup isn't accepting new subscribers right now."),
            _ => Respond(StatusCodes.Status200OK, checkInbox)
        };
    }

    private static async Task<IResult> ConfirmAsync(string token, HttpContext context, IArticleAlertService alerts, ICmsBuilderService cms, IConfiguration configuration)
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        var outcome = await alerts.ConfirmAsync(token, context.RequestAborted);
        return outcome switch
        {
            ArticleAlertConfirmOutcome.Confirmed or ArticleAlertConfirmOutcome.AlreadyConfirmed => await PageAsync(context, cms, configuration,
                "You're subscribed", "You're subscribed",
                "<p>Thanks for confirming. You'll get an email whenever a new article is published.</p><p><a href=\"/blog\">Browse the latest articles</a></p>"),
            ArticleAlertConfirmOutcome.Expired => await PageAsync(context, cms, configuration, "Link expired", "That link has expired",
                "<p>Confirmation links last 7 days. Please sign up again and we'll send a fresh one.</p>", StatusCodes.Status410Gone),
            _ => await PageAsync(context, cms, configuration, "Link not recognized", "That link isn't valid",
                "<p>Please sign up again and we'll send you a new confirmation link.</p>", StatusCodes.Status400BadRequest)
        };
    }

    private static async Task<IResult> UnsubscribeAsync(string token, HttpRequest request, HttpContext context, IArticleAlertService alerts, ICmsBuilderService cms, IConfiguration configuration)
    {
        var ok = await alerts.UnsubscribeAsync(token, context.RequestAborted);
        // A one-click POST from a mail provider (RFC 8058) just needs a status code.
        var isOneClick = request.HasFormContentType
            && (await request.ReadFormAsync(context.RequestAborted))["List-Unsubscribe"] == "One-Click";
        if (isOneClick) return ok ? Results.Ok() : Results.BadRequest();

        return ok
            ? await PageAsync(context, cms, configuration, "Unsubscribed", "You're unsubscribed",
                "<p>You won't receive any more new-article emails. Changed your mind? You can sign up again on the site any time.</p>")
            : await PageAsync(context, cms, configuration, "Link not recognized", "That link isn't valid",
                "<p>We couldn't find a subscription for this link. If you're still receiving emails, reply to one and we'll remove you.</p>", StatusCodes.Status400BadRequest);
    }

    private static IResult PageResult(HttpContext context, ICmsBuilderService cms, IConfiguration configuration, string title, string heading, string bodyHtml, int status) =>
        new DeferredResult(() => PageAsync(context, cms, configuration, title, heading, bodyHtml, status));

    // Rendered in the live website's own layout and theme, like the contact thank-you page.
    private static async Task<IResult> PageAsync(HttpContext context, ICmsBuilderService cms, IConfiguration configuration,
        string title, string heading, string bodyHtml, int status = StatusCodes.Status200OK)
    {
        context.Response.Headers.CacheControl = "no-store";
        var site = await cms.GetSiteBySlugAsync(configuration["Canvas:SiteSlug"] ?? "grantwatson-dev", context.RequestAborted);
        var body = $"""<main class="page-not-found"><h1>{WebUtility.HtmlEncode(heading)}</h1>{bodyHtml}</main>""";
        var html = site is null
            ? PublicSiteHtmlRenderer.Layout(title, string.Empty, null, body)
            : PublicSiteHtmlRenderer.Layout(title, string.Empty, null, body,
                PublicSiteHtmlRenderer.ParseNavItems(site.NavMenuJson),
                PublicSiteHtmlRenderer.ParseFooterNavItems(site.FooterNavMenuJson),
                site.AccentColorHex, site.FontPairingKey, siteName: site.Name,
                logoUrl: site.LogoUrl, faviconUrl: site.FaviconUrl,
                tokens: DesignTokenJson.ParseOrEmpty(site.DesignTokensJson));
        return Results.Content(html, "text/html", statusCode: status);
    }

    private sealed class DeferredResult(Func<Task<IResult>> factory) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext) => await (await factory()).ExecuteAsync(httpContext);
    }
}
