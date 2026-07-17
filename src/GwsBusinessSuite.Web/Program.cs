using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.LiveShow;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Resume;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Services;
using GwsBusinessSuite.Web.Components;
using GwsBusinessSuite.Web.Hubs;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;


// Docker-build-time hook: `dotnet GwsBusinessSuite.Web.dll --install-playwright-browsers`
// installs the Chromium binary Microsoft.Playwright needs (LocalEventsScraperService)
// without requiring PowerShell, which the mcr.microsoft.com/dotnet/aspnet base image
// doesn't have (the usual playwright.ps1 install script needs it). Must exit before
// touching WebApplication.CreateBuilder/the DB/anything else below.
if (args is ["--install-playwright-browsers"])
{
    Environment.Exit(Microsoft.Playwright.Program.Main(["install", "chromium"]));
}

var builder = WebApplication.CreateBuilder(args);
var configuredPathBase = builder.Configuration["Hosting:PathBase"];

// Add services to the container.
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// Compress server-rendered HTML, JSON/API responses, and static assets. Forwarded
// headers surface the original HTTPS scheme behind Cloudflare, so HTTPS compression
// must be enabled explicitly or production traffic would silently skip it.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy(OutputCachePublicContentInvalidator.Tag, policy => policy
        .Expire(TimeSpan.FromMinutes(2))
        .Tag(OutputCachePublicContentInvalidator.Tag));
});
builder.Services.AddSingleton<IPublicContentCacheInvalidator, OutputCachePublicContentInvalidator>();
builder.Services.AddSingleton<PerformanceTelemetry>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddSignalR();

// Content Studio article generation can take several minutes against Ollama
// (first-time model load especially). Extend the circuit's disconnect grace
// period well past that so a brief network blip over the Cloudflare Tunnel
// doesn't tear down the circuit mid-generation.
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
});

builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

// The public React site (apps/public-site) calls /api/blog and /og-image directly
// against this backend's absolute URL rather than through Netlify's redirect-based
// proxy, which proved unreliable. Allow its origins to read those public endpoints.
builder.Services.AddCors(options =>
{
    // The public site only ever issues plain GET fetches against /api/blog and
    // /og-image with no custom headers, so the policy is scoped to that rather
    // than the broader AllowAnyHeader/AllowAnyMethod defaults.
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(
            "https://grantwatson.dev",
            "https://www.grantwatson.dev",
            "http://localhost:5173")
        .WithMethods("GET")
        .WithHeaders("Content-Type"));
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        // SameAsRequest (the framework default) is made explicit here rather than left
        // implicit: it correctly marks the cookie Secure once UseForwardedHeaders (added
        // below) surfaces the real scheme via X-Forwarded-Proto behind Cloudflare Tunnel.
        // Deliberately NOT forced to Always - this container is also reachable over plain
        // HTTP directly (no TLS termination confirmed in front of it yet as of this
        // writing), and forcing Secure would make the browser silently drop the cookie
        // and break login in that case.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin));

    options.AddPolicy("ContentAccess", policy =>
        policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin, AppRoles.Author, AppRoles.Contributor));

    options.AddPolicy("ContributorAccess", policy =>
        policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin, AppRoles.Contributor));

    options.FallbackPolicy = options.GetPolicy("AdminOnly");
});

builder.Services.AddHealthChecks()
    .AddCheck<GwsBusinessSuite.Web.HealthChecks.DatabaseHealthCheck>("database")
    .AddCheck<GwsBusinessSuite.Web.HealthChecks.OllamaHealthCheck>("ollama");

// HeaderName enables validating JSON API requests (which can't carry a hidden form
// field) via the X-CSRF-TOKEN header instead, for the /admin/api/articles endpoints.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window               = TimeSpan.FromMinutes(15),
                PermitLimit          = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Unauthenticated GET endpoints that hit the DB (and, for /og-image and /media,
    // decode base64 blobs up to several MB) on every request. Generous since real
    // visitors and crawlers hit these often, but bounded so a single IP can't hammer them.
    options.AddPolicy("public-read", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window               = TimeSpan.FromMinutes(1),
                PermitLimit          = 120,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Authenticated admin endpoints that mutate data. RequireAuthorization() already
    // blocks unauthenticated callers; this just dampens abuse from a single compromised
    // or scripted session.
    options.AddPolicy("admin-mutation", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window               = TimeSpan.FromMinutes(1),
                PermitLimit          = 30,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Live Show's recording-chunk upload fires every ~3 seconds for the whole duration of
    // a broadcast (see liveShow.js's MediaRecorder timeslice) - the standard 30/min
    // admin-mutation budget is too tight a margin for that cadence (any jitter risks
    // silently dropping chunks with no client-side retry), so this is a separate, more
    // generous budget instead of leaving the endpoint unlimited.
    options.AddPolicy("live-show-chunk", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window               = TimeSpan.FromMinutes(1),
                PermitLimit          = 120,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Unauthenticated POST that writes to the database (public "form" widget submissions) —
    // a much smaller budget than read traffic to keep it useless for spam/flood attempts.
    options.AddPolicy("public-write", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window               = TimeSpan.FromMinutes(5),
                PermitLimit          = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Only the login page wants a redirect on rejection; API-shaped endpoints should get
    // a plain 429 instead of being bounced to an HTML page.
    options.OnRejected = (context, _) =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/admin/login"))
        {
            context.HttpContext.Response.Redirect("/admin/login?error=ratelimit");
        }
        else
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        }

        return ValueTask.CompletedTask;
    };
});
var app = builder.Build();

var normalizedPathBase = NormalizePathBase(configuredPathBase);

// grantwatson.dev is served by this same app/process (see the RequireHost-gated public
// endpoints below) rather than the retired React app on Netlify — distinguished purely by
// Host header, since admin.gwsapp.net keeps its existing admin-only behavior otherwise.
string[] publicHosts = ["grantwatson.dev", "www.grantwatson.dev"];
bool IsPublicHost(HttpContext ctx) => publicHosts.Contains(ctx.Request.Host.Host, StringComparer.OrdinalIgnoreCase);

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var dbContext = await dbFactory.CreateDbContextAsync();
    await dbContext.Database.MigrateAsync();

    await EnsureGrantWatsonHomepageAsync(dbContext, app.Configuration, app.Logger);
    await EnsureAboutPageResumeSectionAsync(dbContext, app.Logger);

    if (!await dbContext.AppUsers.AnyAsync())
    {
        var hasher        = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        var seedUsername  = app.Configuration["AdminAuth:Username"]?.Trim();
        var seedPassword  = app.Configuration["AdminAuth:Password"];

        if (!string.IsNullOrWhiteSpace(seedUsername) && !string.IsNullOrWhiteSpace(seedPassword))
        {
            if (IsWeakSeedPassword(seedPassword, seedUsername, out var weakReason))
            {
                app.Logger.LogError(
                    "Refusing to seed the admin account: AdminAuth:Password {Reason}. " +
                    "Set a stronger password in configuration and restart.",
                    weakReason);
            }
            else
            {
                var admin = new AppUser { Username = seedUsername, Role = AppRoles.Admin, CreatedBy = "system" };
                admin.PasswordHash = hasher.HashPassword(admin, seedPassword);
                dbContext.AppUsers.Add(admin);
                await dbContext.SaveChangesAsync();
            }
        }
    }
}

// Configure the HTTP request pipeline.

// Must run before anything that reads RemoteIpAddress/Scheme (rate limiting, auth,
// HSTS) - otherwise every rate-limit partition below keys on the proxy's IP instead of
// the real client's, effectively collapsing all per-IP limits (including the login
// attempt limiter) into one shared bucket. Only honors X-Forwarded-For/-Proto when the
// *immediate* connecting peer is loopback or a Docker-assigned private-network address
// (covers both "cloudflared on the same droplet host, forwarding to localhost" and
// "cloudflared as a sibling container on the compose network" - see docker-compose.yml).
// If nothing is actually proxying yet, requests arrive directly from real external IPs,
// which never match these ranges, so the header is correctly ignored and RemoteIpAddress
// stays the real client IP either way.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
forwardedHeadersOptions.KnownProxies.Add(System.Net.IPAddress.Loopback);
forwardedHeadersOptions.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
app.UseForwardedHeaders(forwardedHeadersOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// grantwatson.dev gets its own styled 404 (matching public-site.css) instead of the
// admin's Bootstrap-styled not-found page — handled by one target endpoint that branches
// internally on IsPublicHost, not two UseWhen-branched re-execute targets. The two-branch
// version reliably broke auth on the admin side: a re-executed request replayed inside a
// UseWhen branch somehow lost track of the target endpoint's AllowAnonymous metadata by the
// time it reached UseAuthorization, bouncing every admin-side 404 through a login redirect.
app.UseStatusCodePagesWithReExecute("/__not-found", createScopeForStatusCodePages: true);
// HTTP-only in Docker — remove HTTPS redirect so the container runs cleanly on port 8080

// Serve Blazor static assets before auth checks.
app.UseResponseCompression();
app.UseStaticFiles();

if (!string.IsNullOrWhiteSpace(normalizedPathBase))
{
    app.UsePathBase(normalizedPathBase);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await next(context);
    }
    finally
    {
        timer.Stop();
        context.RequestServices.GetRequiredService<PerformanceTelemetry>().Record(context, timer.Elapsed);
    }
});
app.UseOutputCache();
app.UseAntiforgery();
app.UseRateLimiter();

// Plain-text Healthy/Degraded/Unhealthy with no detail disclosure, intended for the deploy
// pipeline's post-deploy smoke test and any external uptime monitor.
app.MapHealthChecks("/health").AllowAnonymous();

app.MapGet("/admin/api/performance", (
    PerformanceTelemetry telemetry,
    GwsBusinessSuite.Application.NewsIntelligence.INewsIntelligenceService newsService) =>
    Results.Ok(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        routes = telemetry.Snapshot(),
        newsRefresh = newsService.GetRefreshStatus()
    }))
    .RequireAuthorization("AdminOnly")
    .RequireRateLimiting("public-read");

// AllowAnonymous at the connection level - unauthenticated viewers must be able to open
// this connection to call JoinAsViewer(inviteToken); JoinAsBroadcaster separately checks
// Context.User's role itself (see LiveShowHub) since the two roles share one hub.
app.MapHub<LiveShowHub>("/hubs/live-show").AllowAnonymous();

app.MapPost("/auth/login", async (
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IUserManagementService userManagementService) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.LocalRedirect("/admin/login?error=invalid");
    }

    var form      = await httpContext.Request.ReadFormAsync();
    var username  = form["username"].ToString().Trim();
    var password  = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    var safeReturn = IsSafeLocalPath(returnUrl) ? returnUrl : "/admin";

    // Per-account lockout (LoginLockoutPolicy) lives behind AttemptLoginAsync, on top of
    // the "login" rate-limit policy's per-IP window - the global limiter alone doesn't
    // stop a distributed attempt targeting one specific account.
    var attempt = await userManagementService.AttemptLoginAsync(username, password);

    if (attempt.IsLockedOut)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((attempt.LockoutRemaining ?? TimeSpan.Zero).TotalMinutes));
        return Results.LocalRedirect(
            $"/admin/login?error=locked&minutes={minutes}&returnUrl={Uri.EscapeDataString(safeReturn)}");
    }

    if (!attempt.Succeeded || attempt.User is null)
    {
        return Results.LocalRedirect($"/admin/login?error=invalid&returnUrl={Uri.EscapeDataString(safeReturn)}");
    }

    var user = attempt.User;
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role),
        new("UserId", user.Id.ToString())
    };

    var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

    var defaultUrl = user.Role == AppRoles.Admin ? "/admin" : "/admin/content-studio";
    return Results.LocalRedirect(IsSafeLocalPath(returnUrl) ? returnUrl : defaultUrl);
}).AllowAnonymous().RequireRateLimiting("login");

app.MapGet("/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/admin/login");
}).AllowAnonymous();

// Serve hero images stored as base64 data URIs as real image responses
// so og:image tags have a cacheable URL that social crawlers can fetch.
// Checks the Article table first (published articles), then falls back to SeoArticleDraft.
app.MapGet("/og-image/{slug}", async (
    string slug,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    var dataUri = await db.Articles
        .Where(a => a.Slug == slug && a.HeroImageDataUri != "")
        .Select(a => a.HeroImageDataUri)
        .FirstOrDefaultAsync()
        ?? await db.SeoArticleDrafts
            .Where(a => a.Slug == slug && a.HeroImageDataUri != "")
            .Select(a => a.HeroImageDataUri)
            .FirstOrDefaultAsync();

    if (string.IsNullOrWhiteSpace(dataUri)) return Results.NotFound();

    var commaIdx = dataUri.IndexOf(',');
    if (commaIdx < 0) return Results.NotFound();

    var header = dataUri[..commaIdx];
    var base64  = dataUri[(commaIdx + 1)..];

    var mime = "image/png";
    var headerParts = header.Split(':');
    if (headerParts.Length > 1)
    {
        var typePart = headerParts[1].Split(';')[0];
        if (!string.IsNullOrWhiteSpace(typePart)) mime = typePart;
    }

    byte[] bytes;
    try { bytes = Convert.FromBase64String(base64); }
    catch { return Results.NotFound(); }

    return Results.Bytes(bytes, mime);
}).AllowAnonymous().RequireRateLimiting("public-read")
    .CacheOutput(OutputCachePublicContentInvalidator.Tag);

// Serves a CmsSite/CmsPage built via the admin Pages screens ("/admin/pages") as a
// standalone public HTML page. These pages are independent of the apps/public-site React
// app — the structured builder targets sites that don't have a hand-written frontend.
// Catch-all so nested pages resolve (see GetPageByFullPathAsync) — a bare {pageSlug} segment
// could only ever look up by slug, which is ambiguous now that slugs are unique per-parent,
// not per-site. Authenticated requests (the Studio's same-origin preview iframe carries the
// admin session cookie) can see Draft pages; anonymous ones can't — this is also what lets
// this route double as the Studio's live preview without a separate draft-preview mechanism.
app.MapGet("/cms/{siteSlug}/{**pageSlug}", async (
    string siteSlug,
    string pageSlug,
    HttpContext httpContext,
    ICmsBuilderService cmsBuilderService,
    GlobalBlockResolver globalBlockResolver,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var includeUnpublished = httpContext.User.Identity?.IsAuthenticated == true;
    var page = await cmsBuilderService.GetPageByFullPathAsync(site.Id, pageSlug, includeUnpublished);
    if (page is null) return Results.NotFound();

    // Edit-mode markup/script (Canvas Studio's live-preview click-to-select) must never
    // reach a real visitor - gated on BOTH an explicit query flag AND the same role check
    // as the Studio page itself ([Authorize(Policy = "ContributorAccess")]), not just
    // "authenticated", so a Contributor casually browsing their own live page in another
    // tab doesn't silently render editable overlays.
    var isStudioUser = httpContext.User.Identity?.IsAuthenticated == true
        && (httpContext.User.IsInRole(AppRoles.Admin) || httpContext.User.IsInRole(AppRoles.Contributor));
    var editMode = isStudioUser && httpContext.Request.Query["edit"] == "1";

    var layout = CmsBuilderJson.ParseLayout(page.BlocksJson);
    if (layout is not null)
    {
        await globalBlockResolver.ResolveAsync(site.Id, layout);
    }
    // Only queries Articles when the page actually has a posts-grid widget - this route is
    // hit on every public page view, so skipping the full-table load for the common case of
    // no posts-grid widget matters here in a way it doesn't for the one-off export below.
    var articles = CmsBlockHtmlRenderer.LayoutContainsPostsGrid(layout)
        ? await LoadPublicArticleSummariesAsync(dbFactory)
        : [];
    var bodyHtml = CmsBlockHtmlRenderer.Render(layout, siteSlug, pageSlug, editMode, articles);
    var pageTitle = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle);
    var metaDescription = System.Net.WebUtility.HtmlEncode(page.MetaDescription);
    var ogImageTag = string.IsNullOrWhiteSpace(page.OgImageUrl)
        ? string.Empty
        : $"""<meta property="og:image" content="{System.Net.WebUtility.HtmlEncode(page.OgImageUrl)}" />""";

    // Site CSS first so page-level CSS can override it for this one page. Custom CSS is
    // never HTML-encoded (that would break the CSS itself), so the only defense needed
    // against breaking out of the <style> tag is stripping any literal "</style" sequence.
    var customCss = string.Join('\n', new[] { site.CustomCss, page.CustomCss }.Where(css => !string.IsNullOrWhiteSpace(css)));
    var customStyleTag = string.IsNullOrWhiteSpace(customCss)
        ? string.Empty
        : $"<style>{SanitizeInlineCss(customCss)}</style>";
    var editModeTag = editMode ? CmsBlockHtmlRenderer.BuildEditModeScript() : string.Empty;

    var html = $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{pageTitle}</title>
          <meta name="description" content="{metaDescription}" />
          {ogImageTag}
          <link rel="stylesheet" href="/cms-public.css" />
          {customStyleTag}
        </head>
        <body>
          {bodyHtml}
          {editModeTag}
        </body>
        </html>
        """;

    return Results.Content(html, "text/html");
}).AllowAnonymous().RequireRateLimiting("public-read")
    .CacheOutput(OutputCachePublicContentInvalidator.Tag);

// Handles "form" widget submissions (see CmsBlockHtmlRenderer's "form" case). Fixed URL
// per site rather than per page — the submitted page's (possibly nested) full path travels
// as a hidden "_path" field instead, since it can't appear in the URL before a fixed
// "/submit" segment once the live site's page route is a catch-all (see RenderForm in
// CmsBlockHtmlRenderer.cs). Tightly rate-limited since it's an unauthenticated POST that
// writes to the database — a much smaller budget than ordinary page views.
app.MapPost("/cms/{siteSlug}/submit", async (
    string siteSlug,
    HttpRequest request,
    HttpContext httpContext,
    ICmsBuilderService cmsBuilderService,
    IFormSubmissionService formSubmissionService) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var form = await request.ReadFormAsync();
    var path = form["_path"].ToString();

    // Lets an authenticated admin test a draft page's form from the Studio preview; an
    // anonymous visitor can never reach that far since the draft page itself already 404s
    // for them before a form could be submitted.
    var includeUnpublished = httpContext.User.Identity?.IsAuthenticated == true;
    var page = await cmsBuilderService.GetPageByFullPathAsync(site.Id, path, includeUnpublished);
    if (page is null) return Results.NotFound();

    // The same form widget renders on both the bare /cms/ fallback and the real public
    // site (RequireHost-gated routes below) — send visitors back to whichever one they
    // came from instead of always landing on the unstyled fallback.
    var thanksUrl = IsPublicHost(httpContext) ? $"/{path}?submitted=1" : $"/cms/{siteSlug}/{path}";

    // Honeypot: a hidden field real visitors never see or fill. A non-empty value means a
    // bot filled every field it found — accept silently so the bot doesn't learn it failed.
    if (!string.IsNullOrWhiteSpace(form["company"]))
    {
        return Results.Redirect(thanksUrl);
    }

    // The form widget's fields are admin-defined per page, so collect whatever was
    // actually posted (minus the honeypot and the routing field) rather than assuming
    // fixed field names.
    var fields = form
        .Where(kvp => kvp.Key != "company" && kvp.Key != "_path")
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

    try
    {
        await formSubmissionService.SubmitAsync(page.Id, fields);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Redirect(thanksUrl);
}).AllowAnonymous().RequireRateLimiting("public-write");

// ── grantwatson.dev — the real public site ──────────────────────────────────────────
// Same app/process as admin.gwsapp.net, gated to only activate for the public host (see
// IsPublicHost above) so admin.gwsapp.net's existing routes are completely unaffected.
// See PublicSiteHtmlRenderer.cs for why this reuses the /cms/ rendering path
// (CmsBlockHtmlRenderer) instead of a separate frontend app.
//
// "/" is handled by the single MapGet("/", ...) further down (it branches on IsPublicHost
// internally) rather than a second RequireHost-gated one here — two endpoints on the exact
// same template, one host-restricted and one not, both stay "valid" candidates for a
// matching host under ASP.NET Core's host matching, which throws AmbiguousMatchException.
//
// {**pageSlug} is a catch-all (binds "services/web-dev", not just one segment) so nested
// Canvas pages resolve — see ICmsBuilderService.GetPageByFullPathAsync. A catch-all must be
// the last route segment, which is why there's no separate "/{pageSlug}/thanks" route
// anymore (a fixed segment can't follow it) — form submissions redirect back to the page
// itself with ?submitted=1 instead, handled inline by RenderPublicCanvasPageAsync.

app.MapGet("/{**pageSlug}", (
        string pageSlug,
        HttpRequest request,
        ICmsBuilderService cmsBuilderService,
        GlobalBlockResolver globalBlockResolver,
        IConfiguration configuration,
        IDbContextFactory<ApplicationDbContext> dbFactory) =>
        RenderPublicCanvasPageAsync(pageSlug, request, request.Query["submitted"] == "1", cmsBuilderService, globalBlockResolver, configuration, dbFactory))
    .RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read")
    .CacheOutput(OutputCachePublicContentInvalidator.Tag);

app.MapGet("/blog", async (
        HttpRequest request,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ICmsBuilderService cmsBuilderService,
        ISiteSettingsService siteSettingsService,
        IConfiguration configuration,
        string? keyword,
        string? category,
        string? tag,
        int page = 1,
        int? pageSize = null) =>
    {
        page = page < 1 ? 1 : page;
        // A missing ?pageSize= falls back to the site's configured default (Settings >
        // Reading) rather than a hardcoded 10 - an explicit query param still overrides it.
        var effectivePageSize = pageSize ?? (await siteSettingsService.GetSettingsAsync()).PostsPerPage;
        effectivePageSize = effectivePageSize is 10 or 12 or 25 or 50 ? effectivePageSize : 12;

        await using var db = await dbFactory.CreateDbContextAsync();
        var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var articles = await db.Articles
            .AsNoTracking()
            .Where(a => a.TrashedAt == null
                && a.Status == ArticleStatuses.Published
                && a.PublishedAtUnixSeconds != null
                && a.PublishedAtUnixSeconds <= nowUnixSeconds)
            .OrderByDescending(a => a.PublishedAtUnixSeconds)
            .ToListAsync();

        var categoryLookup = await db.ArticleCategories.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var categories = categoryLookup.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new PublicSiteHtmlRenderer.CategorySummary(c.Name, c.Slug))
            .ToList();
        var categoryIdBySlug = categoryLookup.Values.ToDictionary(c => c.Slug, c => c.Id);

        var keywords = articles
            .Select(a => a.PrimaryKeyword)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct()
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filtered = articles;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            filtered = filtered.Where(a => a.PrimaryKeyword == keyword).ToList();
        }
        if (!string.IsNullOrWhiteSpace(category) && categoryIdBySlug.TryGetValue(category, out var categoryId))
        {
            filtered = filtered.Where(a => a.CategoryId == categoryId).ToList();
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            filtered = filtered.Where(a => ParseTags(a.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)effectivePageSize));
        page = Math.Min(page, totalPages);

        var pageItems = filtered
            .Skip((page - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(a =>
            {
                var articleCategory = a.CategoryId.HasValue && categoryLookup.TryGetValue(a.CategoryId.Value, out var c) ? c : null;
                return new PublicSiteHtmlRenderer.ArticleSummary(
                    a.Slug, a.Title, a.MetaDescription, a.PrimaryKeyword, a.EstimatedReadingTime, a.PublishedAt,
                    a.HeroImageUrl ?? (a.HeroImageDataUri != "" ? $"/og-image/{a.Slug}" : null),
                    articleCategory?.Name, articleCategory?.Slug, ParseTags(a.Tags));
            })
            .ToList();

        var navMenus = await GetPublicNavMenusAsync(cmsBuilderService, configuration);
        var bodyHtml = PublicSiteHtmlRenderer.BlogListBody(pageItems, keywords, keyword, categories, category, tag, page, effectivePageSize, filtered.Count, totalPages);
        var html = PublicSiteHtmlRenderer.Layout(
            "Blog — Grant Watson",
            "Thoughts on software, building products, and the web.",
            null,
            bodyHtml,
            navMenus.Primary,
            navMenus.Footer,
            navMenus.AccentColorHex,
            navMenus.FontPairingKey,
            canonicalUrl: CombineAbsoluteUrl(GetPublicBaseUrl(configuration, request), $"{request.Path}{request.QueryString}"),
            siteName: navMenus.SiteName,
            logoUrl: navMenus.LogoUrl,
            faviconUrl: navMenus.FaviconUrl);
        return Results.Content(html, "text/html");
    })
    .RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/blog/{slug}", async (
        string slug,
        HttpRequest request,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ICmsBuilderService cmsBuilderService,
        ICommentService commentService,
        IConfiguration configuration) =>
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var a = await db.Articles
            .Include(x => x.AffiliatePlacements)
            .Where(x => x.Slug == slug && x.TrashedAt == null)
            .FirstOrDefaultAsync();

        var navMenus = await GetPublicNavMenusAsync(cmsBuilderService, configuration);

        if (a is null || !IsArticlePubliclyVisible(a, DateTimeOffset.UtcNow))
        {
            return Results.Content(
                PublicSiteHtmlRenderer.Layout(
                    "404 — Not Found",
                    string.Empty,
                    null,
                    PublicSiteHtmlRenderer.NotFoundBody("Article not found.", "/blog", "Back to Blog"),
                    navMenus.Primary,
                    navMenus.Footer,
                    navMenus.AccentColorHex,
                    navMenus.FontPairingKey,
                    siteName: navMenus.SiteName,
                    logoUrl: navMenus.LogoUrl,
                    faviconUrl: navMenus.FaviconUrl),
                "text/html", statusCode: StatusCodes.Status404NotFound);
        }

        var heroImageUrl = a.HeroImageUrl ?? (a.HeroImageDataUri != "" ? $"/og-image/{a.Slug}" : null);
        var renderedMarkdown = ArticleMarkdownRenderer.Render(a.BodyMarkdown, a.AffiliatePlacements.OrderBy(p => p.SortOrder).ToList());
        var articleCategory = a.CategoryId.HasValue
            ? await db.ArticleCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == a.CategoryId.Value)
            : null;
        var approvedComments = await commentService.ListApprovedForArticleAsync(a.Id);
        var replyToCommentId = Guid.TryParse(request.Query["replyTo"], out var parsedReplyToId) ? parsedReplyToId : (Guid?)null;
        var replyComment = replyToCommentId.HasValue ? FindCommentById(approvedComments, replyToCommentId.Value) : null;

        var bodyHtml = PublicSiteHtmlRenderer.BlogPostBody(
            a.Title, a.MetaDescription, a.Author, a.PublishedAt, a.EstimatedReadingTime, a.PrimaryKeyword,
            heroImageUrl, a.HeroImageAltText, a.HeroImageCaption, renderedMarkdown,
            articleCategory?.Name, articleCategory?.Slug, ParseTags(a.Tags), a.Slug, approvedComments,
            replyToCommentId: replyComment?.Id,
            replyToAuthorName: replyComment?.AuthorName);

        if (request.Query["comment"] == "1")
        {
            bodyHtml = PublicSiteHtmlRenderer.CommentPendingBanner() + bodyHtml;
        }

        var html = PublicSiteHtmlRenderer.Layout(
            a.Title,
            a.MetaDescription,
            heroImageUrl,
            bodyHtml,
            navMenus.Primary,
            navMenus.Footer,
            navMenus.AccentColorHex,
            navMenus.FontPairingKey,
            canonicalUrl: CombineAbsoluteUrl(GetPublicBaseUrl(configuration, request), $"/blog/{a.Slug}"),
            siteName: navMenus.SiteName,
            logoUrl: navMenus.LogoUrl,
            faviconUrl: navMenus.FaviconUrl);
        return Results.Content(html, "text/html");
    })
    .RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

// Handles the public comment form's POST from BlogPostBody. Looks the article up by slug
// (not a client-supplied id) so a forged/stale id can't attach a comment to the wrong
// article, matching the read route's own lookup. Honeypot + rate limit mirror the CMS
// "form" widget's submission endpoint above.
app.MapPost("/blog/{slug}/comments", async (
    string slug,
    HttpRequest request,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICommentService commentService) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles
        .Where(x => x.Slug == slug && x.TrashedAt == null)
        .FirstOrDefaultAsync();
    if (article is null || !IsArticlePubliclyVisible(article, DateTimeOffset.UtcNow)) return Results.NotFound();

    var form = await request.ReadFormAsync();
    var thanksUrl = $"/blog/{slug}?comment=1";

    // Honeypot: a hidden field real visitors never see or fill. A non-empty value means a
    // bot filled every field it found — accept silently so the bot doesn't learn it failed.
    if (!string.IsNullOrWhiteSpace(form["website"]))
    {
        return Results.Redirect(thanksUrl);
    }

    try
    {
        await commentService.SubmitAsync(
            article.Id,
            form["authorName"].ToString(),
            form["authorEmail"].ToString(),
            form["body"].ToString(),
            Guid.TryParse(form["parentCommentId"], out var parentCommentId) ? parentCommentId : null);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Redirect(thanksUrl);
}).RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-write");

// Public-facing affiliate click-tracking redirect: records a click against the
// placement, then forwards to its real CJ tracking URL. Rendered article markup links
// here (see ArticleMarkdownRenderer.BuildCardMarkup) instead of the raw CJ URL directly.
app.MapGet("/go/{placementId:guid}", async (
    Guid placementId,
    IAffiliateAnalyticsService affiliateAnalyticsService) =>
{
    var destinationUrl = await affiliateAnalyticsService.RecordClickAsync(placementId);
    return destinationUrl is null
        ? Results.Redirect("/", permanent: false)
        : Results.Redirect(destinationUrl, permanent: false);
}).RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/resume", () => Results.Redirect("/about#resume", permanent: true))
    .RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/resume.pdf", (IResumePdfService resumePdfService) =>
        Results.File(resumePdfService.GenerateResumePdf(), "application/pdf", "Grant-Watson-Resume.pdf"))
    .RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/sitemap.xml", async (
    HttpRequest request,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICmsBuilderService cmsBuilderService,
    IConfiguration configuration) =>
{
    var site = await GetConfiguredCanvasSiteAsync(cmsBuilderService, configuration);
    if (site is null)
    {
        return Results.NotFound();
    }

    var baseUrl = GetPublicBaseUrl(configuration, request);
    var allPages = (await cmsBuilderService.ListPagesAsync(site.Id)).ToList();
    var visiblePages = allPages
        .Where(page => IsCmsPagePubliclyVisible(page, DateTimeOffset.UtcNow))
        .Select(page =>
        {
            var fullPath = cmsBuilderService.BuildFullPath(page, allPages);
            var routePath = GetPublicCanvasRoutePath(page, fullPath);
            return new PublicSiteHtmlRenderer.SitemapEntry(
                ResolvePublicUrl(baseUrl, page.CanonicalUrl, routePath),
                page.UpdatedAt ?? page.PublishedAt ?? page.CreatedAt);
        });

    await using var db = await dbFactory.CreateDbContextAsync();
    var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var visibleArticles = (await db.Articles
            .AsNoTracking()
            .Where(article => article.TrashedAt == null
                && article.Status == ArticleStatuses.Published
                && article.PublishedAtUnixSeconds != null
                && article.PublishedAtUnixSeconds <= nowUnixSeconds)
            .ToListAsync())
        .Select(article => new PublicSiteHtmlRenderer.SitemapEntry(
            ResolvePublicUrl(baseUrl, null, $"/blog/{article.Slug}"),
            article.UpdatedAt ?? article.PublishedAt ?? article.CreatedAt));

    var xml = PublicSiteHtmlRenderer.SitemapXml(
        visiblePages.Concat(visibleArticles)
            .OrderByDescending(entry => entry.LastModified)
            .ToList());

    return Results.Text(xml, "application/xml", Encoding.UTF8);
}).RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/robots.txt", (
    HttpRequest request,
    IConfiguration configuration) =>
{
    var sitemapUrl = CombineAbsoluteUrl(GetPublicBaseUrl(configuration, request), "/sitemap.xml");
    return Results.Text(PublicSiteHtmlRenderer.RobotsTxt(sitemapUrl), "text/plain", Encoding.UTF8);
}).RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/rss.xml", async (
    HttpRequest request,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICmsBuilderService cmsBuilderService,
    IConfiguration configuration) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var baseUrl = GetPublicBaseUrl(configuration, request);
    var site = await GetConfiguredCanvasSiteAsync(cmsBuilderService, configuration);
    var siteName = site?.Name ?? configuration["Canvas:SiteName"] ?? "Site";

    var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var items = (await db.Articles
            .AsNoTracking()
            .Where(article => article.TrashedAt == null
                && article.Status == ArticleStatuses.Published
                && article.PublishedAtUnixSeconds != null
                && article.PublishedAtUnixSeconds <= nowUnixSeconds)
            .OrderByDescending(article => article.PublishedAtUnixSeconds)
            .Take(20)
            .ToListAsync())
        .Select(article => new PublicSiteHtmlRenderer.RssItem(
            article.Title,
            CombineAbsoluteUrl(baseUrl, $"/blog/{article.Slug}"),
            article.MetaDescription,
            article.PublishedAt,
            CombineAbsoluteUrl(baseUrl, $"/blog/{article.Slug}")))
        .ToList();

    var xml = PublicSiteHtmlRenderer.RssXml(
        $"{siteName} Blog",
        "Thoughts on software, building products, and the web.",
        CombineAbsoluteUrl(baseUrl, "/blog"),
        items);

    return Results.Text(xml, "application/rss+xml", Encoding.UTF8);
}).RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");
app.MapGet("/feed", (
    HttpRequest request,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICmsBuilderService cmsBuilderService,
    IConfiguration configuration) =>
    Results.Redirect("/rss.xml"))
    .RequireHost(publicHosts).AllowAnonymous().RequireRateLimiting("public-read");

// One re-execute target for every 404 in the app, branching internally on IsPublicHost —
// not two UseWhen-branched re-execute targets (see the note above
// UseStatusCodePagesWithReExecute for why that broke admin-side auth) and not the Blazor
// NotFound.razor component (its own <AuthorizeRouteView> in Routes.razor does a second,
// separate authorization check that doesn't reliably see [AllowAnonymous] on a re-executed
// request either).
app.MapGet("/__not-found", async (HttpContext httpContext, ICmsBuilderService cmsBuilderService, IConfiguration configuration) =>
    {
        if (IsPublicHost(httpContext))
        {
            var navMenus = await GetPublicNavMenusAsync(cmsBuilderService, configuration);
            var html = PublicSiteHtmlRenderer.Layout(
                "404 — Not Found",
                string.Empty,
                null,
                PublicSiteHtmlRenderer.NotFoundBody("Sorry, the content you are looking for does not exist.", "/", "Back to Home"),
                navMenus.Primary,
                navMenus.Footer,
                navMenus.AccentColorHex,
                navMenus.FontPairingKey,
                siteName: navMenus.SiteName,
                logoUrl: navMenus.LogoUrl,
                faviconUrl: navMenus.FaviconUrl);
            return Results.Content(html, "text/html");
        }

        return Results.Content("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Not Found</title>
              <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css" />
            </head>
            <body class="d-flex align-items-center justify-content-center" style="min-height:100vh">
              <div class="text-center">
                <h3>Not Found</h3>
                <p class="text-secondary">Sorry, the content you are looking for does not exist.</p>
                <a href="/admin" class="btn btn-primary btn-sm">Back to Dashboard</a>
              </div>
            </body>
            </html>
            """, "text/html");
    })
    .AllowAnonymous();

// Admin: exports a full CmsSite as a self-contained static ZIP so the output can be
// deployed to any host that serves static files. Each page becomes its own HTML file
// with all CSS inlined (no external links), making the archive fully portable.
app.MapGet("/admin/api/cms/{siteSlug}/export.zip", async (
    string siteSlug,
    HttpRequest request,
    ICmsBuilderService cmsBuilderService,
    IWebHostEnvironment env,
    GlobalBlockResolver globalBlockResolver,
    IMediaLibraryService mediaLibraryService,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    // The posts-grid widget always pulls from this app's own blog (not the exported
    // CmsSite), so a static export can't bundle those articles - they still live on the
    // running app. Rewrite the widget's /blog/{slug} links and /og-image/{slug} thumbnails
    // to absolute URLs back here instead of leaving them as root-relative paths that 404
    // once the export is deployed to a different static host.
    var liveSiteBaseUrl = $"{request.Scheme}://{request.Host}";
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    // allPages (unfiltered beyond trashed) is needed so BuildFullPath can resolve
    // ancestor slugs even for a page whose parent itself isn't publicly visible.
    var allPages = await cmsBuilderService.ListPagesAsync(site.Id);
    var pages = allPages.Where(p => IsCmsPagePubliclyVisible(p, DateTimeOffset.UtcNow)).ToList();
    // Snapshotted once for the whole export, same posture as everything else in a static
    // export being a point-in-time copy (e.g. form widgets still post back to the live site).
    var articles = await LoadPublicArticleSummariesAsync(dbFactory);

    // Read the base stylesheet once — it gets embedded in every page.
    var cssFilePath = Path.Combine(env.WebRootPath, "cms-public.css");
    var baseStylesheet = File.Exists(cssFilePath) ? await File.ReadAllTextAsync(cssFilePath) : string.Empty;

    var mediaReferencePattern = new Regex(
        "/media/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);
    var referencedMediaIds = new HashSet<Guid>();
    var renderedPages = new List<(string EntryPath, string Html)>();

    foreach (var page in pages)
    {
        var layout = CmsBuilderJson.ParseLayout(page.BlocksJson);
        if (layout is not null)
        {
            await globalBlockResolver.ResolveAsync(site.Id, layout);
        }
        // The full nested path (not just the leaf slug) so a form widget's hidden "_path"
        // field matches what the live /cms/{siteSlug}/{**pageSlug} route would have passed.
        var fullPath = cmsBuilderService.BuildFullPath(page, allPages);
        var bodySections = CmsBlockHtmlRenderer.Render(layout, site.Slug, fullPath, articles: articles);
        var pageTitle = System.Net.WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle);
        var metaDescription = System.Net.WebUtility.HtmlEncode(page.MetaDescription);
        var ogImageTag = string.IsNullOrWhiteSpace(page.OgImageUrl)
            ? string.Empty
            : $"""<meta property="og:image" content="{System.Net.WebUtility.HtmlEncode(page.OgImageUrl)}" />""";

        // Combine base + site + page CSS inline so the HTML file is self-contained.
        var combinedCss = string.Join('\n', new[] { baseStylesheet, site.CustomCss, page.CustomCss }
            .Where(css => !string.IsNullOrWhiteSpace(css)));

        var pageHtml = $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{pageTitle}</title>
              <meta name="description" content="{metaDescription}" />
              {ogImageTag}
              <style>{SanitizeInlineCss(combinedCss)}</style>
            </head>
            <body>
              {bodySections}
            </body>
            </html>
            """;

        pageHtml = pageHtml
            .Replace("href=\"/blog/", $"href=\"{liveSiteBaseUrl}/blog/", StringComparison.Ordinal)
            .Replace("src=\"/og-image/", $"src=\"{liveSiteBaseUrl}/og-image/", StringComparison.Ordinal);

        foreach (Match match in mediaReferencePattern.Matches(pageHtml))
        {
            referencedMediaIds.Add(Guid.Parse(match.Groups[1].Value));
        }

        // Each page lives at {fullPath}/index.html so nested pages (e.g. "services/web-dev")
        // resolve correctly on any static host, and the site root gets an index.html that
        // lists all pages.
        var entryPath = string.Equals(fullPath, "index", StringComparison.OrdinalIgnoreCase)
            ? "index.html"
            : $"{fullPath}/index.html";

        renderedPages.Add((entryPath, pageHtml));
    }

    await using var memoryStream = new MemoryStream();
    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
    {
        // Bundle every referenced media asset into the zip so the export is actually
        // self-contained instead of pointing back at this app's /media/{id} route.
        var mediaExportPaths = new Dictionary<Guid, string>();
        foreach (var mediaId in referencedMediaIds)
        {
            var content = await mediaLibraryService.GetContentAsync(mediaId);
            if (content is null)
            {
                continue;
            }

            var extension = content.Value.ContentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                _ => string.Empty
            };
            var mediaEntryPath = $"media/{mediaId}{extension}";
            var mediaEntry = archive.CreateEntry(mediaEntryPath, CompressionLevel.Optimal);
            await using (var mediaEntryStream = mediaEntry.Open())
            {
                await mediaEntryStream.WriteAsync(content.Value.Content);
            }
            mediaExportPaths[mediaId] = $"/{mediaEntryPath}";
        }

        foreach (var (entryPath, html) in renderedPages)
        {
            var rewrittenHtml = mediaExportPaths.Count == 0
                ? html
                : mediaReferencePattern.Replace(html, match =>
                    mediaExportPaths.TryGetValue(Guid.Parse(match.Groups[1].Value), out var exportedPath)
                        ? exportedPath
                        : match.Value);

            var entry = archive.CreateEntry(entryPath, CompressionLevel.SmallestSize);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(rewrittenHtml);
        }

        // Top-level index.html if no page has a slug of "index"
        if (!pages.Any(p => string.Equals(p.Slug, "index", StringComparison.OrdinalIgnoreCase)))
        {
            var pageLinks = string.Concat(pages.Select(p =>
                $"""<li><a href="./{System.Net.WebUtility.HtmlEncode(cmsBuilderService.BuildFullPath(p, allPages))}/">{System.Net.WebUtility.HtmlEncode(p.Title)}</a></li>"""));

            var indexHtml = $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                  <meta charset="utf-8" />
                  <title>{System.Net.WebUtility.HtmlEncode(site.Name)}</title>
                  <style>{SanitizeInlineCss(string.IsNullOrWhiteSpace(site.CustomCss) ? baseStylesheet : baseStylesheet + '\n' + site.CustomCss)}</style>
                </head>
                <body>
                  <section class="cms-block">
                    <h1>{System.Net.WebUtility.HtmlEncode(site.Name)}</h1>
                    <ul>{pageLinks}</ul>
                  </section>
                </body>
                </html>
                """;

            var rootEntry = archive.CreateEntry("index.html", CompressionLevel.SmallestSize);
            await using var rootStream = rootEntry.Open();
            await using var rootWriter = new StreamWriter(rootStream);
            await rootWriter.WriteAsync(indexHtml);
        }
    }

    memoryStream.Position = 0;
    var fileName = $"{siteSlug}-export.zip";
    return Results.File(memoryStream.ToArray(), "application/zip", fileName);
}).RequireAuthorization();

// Decodes and serves a base64-stored media library asset, mirroring the /og-image pattern.
app.MapGet("/media/{id:guid}", async (Guid id, IMediaLibraryService mediaLibraryService) =>
{
    var content = await mediaLibraryService.GetContentAsync(id);
    return content is null ? Results.NotFound() : Results.Bytes(content.Value.Content, content.Value.ContentType);
}).AllowAnonymous().RequireRateLimiting("public-read");

// Falls back to the full-size asset when no thumbnail was generated (pre-thumbnailing
// uploads, or an original already small enough that a copy wasn't worth storing) - see
// MediaLibraryService.GetThumbnailContentAsync.
app.MapGet("/media/{id:guid}/thumb", async (Guid id, IMediaLibraryService mediaLibraryService) =>
{
    var content = await mediaLibraryService.GetThumbnailContentAsync(id);
    return content is null ? Results.NotFound() : Results.Bytes(content.Value.Content, content.Value.ContentType);
}).AllowAnonymous().RequireRateLimiting("public-read");

// Raw request body is one MediaRecorder chunk (see liveShow.js's ondataavailable), appended
// to the session's on-disk recording file in the order the browser calls this - the browser
// awaits each POST before sending the next, so there's no need to buffer/reorder here.
app.MapPost("/admin/api/live-show/{sessionId:guid}/recording-chunk", async (
    Guid sessionId,
    HttpRequest request,
    ILiveShowService liveShowService) =>
{
    await liveShowService.AppendRecordingChunkAsync(sessionId, request.Body);
    return Results.Ok();
}).RequireAuthorization("AdminOnly").RequireRateLimiting("live-show-chunk");

app.MapPost("/admin/api/live-show/{sessionId:guid}/finalize-recording", async (
    Guid sessionId,
    int durationSeconds,
    ILiveShowService liveShowService) =>
{
    await liveShowService.FinalizeRecordingAsync(sessionId, durationSeconds);
    return Results.Ok();
}).RequireAuthorization("AdminOnly").RequireRateLimiting("admin-mutation");

// enableRangeProcessing lets the <video> player seek/scrub instead of only playing linearly.
app.MapGet("/admin/api/live-show/recordings/{recordingId:guid}/file", async (
    Guid recordingId,
    ILiveShowService liveShowService) =>
{
    var filePath = await liveShowService.GetRecordingFilePathAsync(recordingId);
    return filePath is null
        ? Results.NotFound()
        : Results.File(filePath, "video/webm", enableRangeProcessing: true);
}).RequireAuthorization("AdminOnly");

// ── CMS Pages JSON API ────────────────────────────────────────────────────
// Used by the React frontend (apps/public-site) to fetch page content for
// dynamic routes. The React app reads the VITE_CMS_SITE_SLUG env var to know
// which site's pages to load, so Canvas becomes the single
// tool for managing what appears on grantwatson.dev.

app.MapGet("/api/cms/{siteSlug}/pages", async (
    string siteSlug,
    ICmsBuilderService cmsBuilderService) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var pages = (await cmsBuilderService.ListPagesAsync(site.Id))
        .Where(page => IsCmsPagePubliclyVisible(page, DateTimeOffset.UtcNow))
        .ToList();

    return Results.Ok(pages.Select(page => new
    {
        page.Slug,
        page.Title,
        MetaTitle = string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle,
        page.MetaDescription,
        page.OgImageUrl
    }));
}).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/api/cms/{siteSlug}/pages/{pageSlug}", async (
    string siteSlug,
    string pageSlug,
    ICmsBuilderService cmsBuilderService) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var page = await cmsBuilderService.GetPageBySlugAsync(site.Id, pageSlug);
    // Likely unused now that the React frontend is retired (AllowAnonymous JSON API), but
    // still shouldn't leak draft content if anything still calls it.
    if (page is null || !IsCmsPagePubliclyVisible(page, DateTimeOffset.UtcNow)) return Results.NotFound();

    // Parse blocks from JSON so the React app gets a proper array, not a raw
    // JSON string — it can iterate blocks directly without a second parse.
    var blocks = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
        string.IsNullOrWhiteSpace(page.BlocksJson) ? "[]" : page.BlocksJson);

    return Results.Ok(new
    {
        page.Slug,
        page.Title,
        MetaTitle = string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle,
        page.MetaDescription,
        page.OgImageUrl,
        SiteCustomCss = site.CustomCss,
        PageCustomCss = page.CustomCss,
        Blocks = blocks
    });
}).AllowAnonymous().RequireRateLimiting("public-read");

// Public JSON API — hydrated from the Article table (SQLite is the source of truth).
app.MapGet("/api/blog", async (IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var articles = await db.Articles
        .AsNoTracking()
        .Where(a => a.TrashedAt == null
            && a.Status == ArticleStatuses.Published
            && a.PublishedAtUnixSeconds != null
            && a.PublishedAtUnixSeconds <= nowUnixSeconds)
        .OrderByDescending(a => a.PublishedAtUnixSeconds)
        .ToListAsync();

    var result = articles
        .Select(a => new
        {
            a.Slug,
            a.Title,
            a.MetaDescription,
            a.PrimaryKeyword,
            a.EstimatedReadingTime,
            a.PublishedAt,
            HasHeroImage = a.HeroImageUrl != null || a.HeroImageDataUri != "",
            HeroImageUrl = a.HeroImageUrl != null
                ? a.HeroImageUrl
                : a.HeroImageDataUri != "" ? $"/og-image/{a.Slug}" : null
        })
        .ToList();

    return Results.Ok(result);
}).AllowAnonymous().RequireRateLimiting("public-read");

app.MapGet("/api/blog/{slug}", async (
    string slug,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var a = await db.Articles
        .Include(x => x.AffiliatePlacements)
        .Where(x => x.Slug == slug && x.TrashedAt == null)
        .FirstOrDefaultAsync();

    if (a is null || !IsArticlePubliclyVisible(a, DateTimeOffset.UtcNow)) return Results.NotFound();

    var heroImageUrl = a.HeroImageUrl
        ?? (a.HeroImageDataUri != "" ? $"/og-image/{a.Slug}" : null);

    var renderedMarkdown = ArticleMarkdownRenderer.Render(
        a.BodyMarkdown,
        a.AffiliatePlacements.OrderBy(p => p.SortOrder).ToList());

    return Results.Ok(new
    {
        a.Slug,
        a.Title,
        a.Topic,
        a.MetaDescription,
        ArticleMarkdown      = renderedMarkdown,
        a.EstimatedReadingTime,
        a.PrimaryKeyword,
        a.SecondaryKeywords,
        a.PublishedAt,
        a.Author,
        HasHeroImage         = heroImageUrl is not null,
        HeroImageUrl         = heroImageUrl,
        a.HeroImageAltText,
        a.HeroImageCaption
    });
}).AllowAnonymous().RequireRateLimiting("public-read");

// Admin: publish an approved SeoArticleDraft to the live blog as an Article.
app.MapPost("/admin/api/articles/publish-draft/{draftId:guid}", async (
    Guid draftId,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IContentStudioService contentStudioService) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    try
    {
        var published = await contentStudioService.PublishDraftToSiteAsync(new DraftPublishRequest
        {
            DraftId = draftId,
            PerformedBy = "content-studio-api"
        });

        return published is null
            ? Results.NotFound()
            : Results.Ok(new { slug = published.Slug });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: issue a CSRF token for the mutation endpoints below. Callers must fetch this
// first and echo the returned token back via the X-CSRF-TOKEN header.
app.MapGet("/admin/api/antiforgery-token", (HttpContext httpContext, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(httpContext);
    return Results.Ok(new { token = tokens.RequestToken });
}).RequireAuthorization();

// Admin: list all articles (any status)
app.MapGet("/admin/api/articles", async (IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var rows = await db.Articles.ToListAsync();
    var articles = rows
        .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
        .Select(a => new
        {
            a.Id, a.Slug, a.Title, a.Status, a.Source, a.PublishedAt,
            UpdatedAt = a.UpdatedAt ?? a.CreatedAt
        })
        .ToList();
    return Results.Ok(articles);
}).RequireAuthorization();

// Admin: get a single article for editing
app.MapGet("/admin/api/articles/{id:guid}", async (Guid id, IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    return article is null ? Results.NotFound() : Results.Ok(article);
}).RequireAuthorization();

// Admin: create a new manual article draft
app.MapPost("/admin/api/articles", async (
    ArticleUpsertRequest req,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserAccessor currentUserAccessor) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    var performedBy = await currentUserAccessor.GetCurrentUsernameAsync();
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = new Article
    {
        Slug              = req.Slug,
        Title             = req.Title,
        Topic             = req.Topic,
        Author            = string.IsNullOrWhiteSpace(req.Author) ? "Grant Watson" : req.Author,
        PrimaryKeyword    = req.PrimaryKeyword,
        SecondaryKeywords = req.SecondaryKeywords,
        MetaDescription   = req.MetaDescription,
        EstimatedReadingTime = req.EstimatedReadingTime,
        HeroImageUrl      = string.IsNullOrWhiteSpace(req.HeroImageUrl) ? null : req.HeroImageUrl,
        HeroImageAltText  = req.HeroImageAltText,
        HeroImageCaption  = req.HeroImageCaption,
        BodyMarkdown      = req.BodyMarkdown,
        Status            = ArticleStatuses.Draft,
        Source            = ArticleSource.Manual,
        CreatedBy         = performedBy
    };
    db.Articles.Add(article);
    await db.SaveChangesAsync();
    return Results.Created($"/admin/api/articles/{article.Id}", new { article.Id, article.Slug });
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: update an existing article
app.MapPut("/admin/api/articles/{id:guid}", async (
    Guid id,
    ArticleUpsertRequest req,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserAccessor currentUserAccessor) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    var performedBy = await currentUserAccessor.GetCurrentUsernameAsync();
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Slug              = req.Slug;
    article.Title             = req.Title;
    article.Topic             = req.Topic;
    article.Author            = string.IsNullOrWhiteSpace(req.Author) ? "Grant Watson" : req.Author;
    article.PrimaryKeyword    = req.PrimaryKeyword;
    article.SecondaryKeywords = req.SecondaryKeywords;
    article.MetaDescription   = req.MetaDescription;
    article.EstimatedReadingTime = req.EstimatedReadingTime;
    article.HeroImageUrl      = string.IsNullOrWhiteSpace(req.HeroImageUrl) ? null : req.HeroImageUrl;
    article.HeroImageAltText  = req.HeroImageAltText;
    article.HeroImageCaption  = req.HeroImageCaption;
    article.BodyMarkdown      = req.BodyMarkdown;
    article.UpdatedBy         = performedBy;
    article.UpdatedAt         = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status });
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: publish an article (sets Status=Published, stamps PublishedAt if not already set)
app.MapPost("/admin/api/articles/{id:guid}/publish", async (
    Guid id,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserAccessor currentUserAccessor) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    var performedBy = await currentUserAccessor.GetCurrentUsernameAsync();
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Status      = ArticleStatuses.Published;
    article.PublishedAt ??= DateTimeOffset.UtcNow;
    article.UpdatedAt   = DateTimeOffset.UtcNow;
    article.UpdatedBy   = performedBy;
    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status, article.PublishedAt });
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: unpublish an article (back to Draft)
app.MapPost("/admin/api/articles/{id:guid}/unpublish", async (
    Guid id,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserAccessor currentUserAccessor) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    var performedBy = await currentUserAccessor.GetCurrentUsernameAsync();
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Status    = ArticleStatuses.Draft;
    article.UpdatedAt = DateTimeOffset.UtcNow;
    article.UpdatedBy = performedBy;
    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status });
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: delete a draft article (published articles must be unpublished first)
app.MapDelete("/admin/api/articles/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();
    if (article.Status == ArticleStatuses.Published)
        return Results.BadRequest(new { error = "Unpublish the article before deleting it." });

    db.Articles.Remove(article);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// "/" on the public host renders the Canvas "home" page; anywhere else (admin.gwsapp.net,
// localhost, direct IP) redirects to /admin as before. One endpoint, not two — see the note
// above the /{**pageSlug} route for why a second RequireHost-gated "/" registration breaks.
app.MapGet("/", (
    HttpContext httpContext,
    HttpRequest request,
    ICmsBuilderService cmsBuilderService,
    GlobalBlockResolver globalBlockResolver,
    IConfiguration configuration,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
    IsPublicHost(httpContext)
        ? RenderPublicCanvasPageAsync("home", request, request.Query["submitted"] == "1", cmsBuilderService, globalBlockResolver, configuration, dbFactory)
        : Task.FromResult(Results.Redirect("/admin")))
    .AllowAnonymous().RequireRateLimiting("public-read")
    .CacheOutput(OutputCachePublicContentInvalidator.Tag);

app.Run();

// Fetches the configured Canvas site and returns its Primary + Footer nav menus plus its
// global design tokens (accent color + font pairing) — shared by every public-host handler
// that renders a full page shell, not just Canvas pages (blog list/post, 404), since all of
// them funnel through PublicSiteHtmlRenderer.Layout.
static async Task<PublicNavMenus> GetPublicNavMenusAsync(ICmsBuilderService cmsBuilderService, IConfiguration configuration)
{
    var site = await GetConfiguredCanvasSiteAsync(cmsBuilderService, configuration);
    return new PublicNavMenus(
        PublicSiteHtmlRenderer.ParseNavItems(site?.NavMenuJson),
        PublicSiteHtmlRenderer.ParseFooterNavItems(site?.FooterNavMenuJson),
        site?.AccentColorHex,
        site?.FontPairingKey,
        site?.Name,
        site?.LogoUrl,
        site?.FaviconUrl);
}

// Renders a Canvas page (by full path, under the Canvas:SiteSlug-configured site) as a full
// grantwatson.dev document — shared by GET "/" (path "home") and GET "/{**pageSlug}".
// fullPath supports nested pages ("services/web-dev") via GetPageByFullPathAsync.
static async Task<IResult> RenderPublicCanvasPageAsync(
    string fullPath,
    HttpRequest request,
    bool showSubmittedBanner,
    ICmsBuilderService cmsBuilderService,
    GlobalBlockResolver globalBlockResolver,
    IConfiguration configuration,
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    var siteSlug = configuration["Canvas:SiteSlug"] ?? string.Empty;
    var site = await GetConfiguredCanvasSiteAsync(cmsBuilderService, configuration);
    if (site is null)
    {
        return Results.Content(
            PublicSiteHtmlRenderer.Layout("404 — Not Found", string.Empty, null, PublicSiteHtmlRenderer.NotFoundBody("Page not found.", "/", "Back to Home")),
            "text/html", statusCode: StatusCodes.Status404NotFound);
    }

    var navItems = PublicSiteHtmlRenderer.ParseNavItems(site.NavMenuJson);
    var footerNavItems = PublicSiteHtmlRenderer.ParseFooterNavItems(site.FooterNavMenuJson);
    var normalizedPath = fullPath.Trim('/');
    // includeUnpublished stays false here — real visitors on grantwatson.dev never see
    // drafts, only the auth-aware /cms/{siteSlug}/{**pageSlug} preview route does.
    var page = await cmsBuilderService.GetPageByFullPathAsync(site.Id, normalizedPath, includeUnpublished: false);
    if (page is null)
    {
        return Results.Content(
            PublicSiteHtmlRenderer.Layout(
                "404 — Not Found",
                string.Empty,
                null,
                PublicSiteHtmlRenderer.NotFoundBody("Page not found.", "/", "Back to Home"),
                navItems,
                footerNavItems,
                site.AccentColorHex,
                site.FontPairingKey,
                siteName: site.Name,
                logoUrl: site.LogoUrl,
                faviconUrl: site.FaviconUrl),
            "text/html", statusCode: StatusCodes.Status404NotFound);
    }

    var layout = CmsBuilderJson.ParseLayout(page.BlocksJson);
    if (layout is not null)
    {
        await globalBlockResolver.ResolveAsync(site.Id, layout);
    }
    var articles = CmsBlockHtmlRenderer.LayoutContainsPostsGrid(layout)
        ? await LoadPublicArticleSummariesAsync(dbFactory)
        : [];
    var bodyHtml = CmsBlockHtmlRenderer.Render(layout, siteSlug, fullPath, articles: articles);
    if (showSubmittedBanner)
    {
        bodyHtml = PublicSiteHtmlRenderer.SubmittedBanner() + bodyHtml;
    }

    var customCss = string.Join('\n', new[] { site.CustomCss, page.CustomCss }.Where(css => !string.IsNullOrWhiteSpace(css)));
    var wrappedBody = string.IsNullOrWhiteSpace(customCss)
        ? bodyHtml
        : $"<style>{SanitizeInlineCss(customCss)}</style>{bodyHtml}";

    var pageTitle = string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle;
    var canonicalUrl = ResolvePublicUrl(
        GetPublicBaseUrl(configuration, request),
        page.CanonicalUrl,
        GetPublicCanvasRoutePath(page, normalizedPath));
    var html = PublicSiteHtmlRenderer.Layout(
        pageTitle,
        page.MetaDescription,
        page.OgImageUrl,
        wrappedBody,
        navItems,
        footerNavItems,
        site.AccentColorHex,
        site.FontPairingKey,
        canonicalUrl: canonicalUrl,
        siteName: site.Name,
        logoUrl: site.LogoUrl,
        faviconUrl: site.FaviconUrl);
    return Results.Content(html, "text/html");
}

static async Task<CmsSite?> GetConfiguredCanvasSiteAsync(ICmsBuilderService cmsBuilderService, IConfiguration configuration)
{
    var siteSlug = configuration["Canvas:SiteSlug"] ?? string.Empty;
    return string.IsNullOrWhiteSpace(siteSlug)
        ? null
        : await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
}

static string GetPublicBaseUrl(IConfiguration configuration, HttpRequest? request = null)
{
    var configured = configuration["Canvas:PublicBaseUrl"]?.Trim();
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.TrimEnd('/');
    }

    if (request is not null && request.Host.HasValue)
    {
        return $"{request.Scheme}://{request.Host.Value}".TrimEnd('/');
    }

    return string.Empty;
}

static string ResolvePublicUrl(string baseUrl, string? overrideUrl, string fallbackPath)
{
    if (!string.IsNullOrWhiteSpace(overrideUrl))
    {
        if (Uri.TryCreate(overrideUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        return CombineAbsoluteUrl(baseUrl, overrideUrl);
    }

    return CombineAbsoluteUrl(baseUrl, fallbackPath);
}

static string CombineAbsoluteUrl(string baseUrl, string path)
{
    if (string.IsNullOrWhiteSpace(path) || path == "/")
    {
        return string.IsNullOrWhiteSpace(baseUrl) ? "/" : baseUrl;
    }

    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return path.StartsWith('/') ? path : $"/{path}";
    }

    return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}

static string GetPublicCanvasRoutePath(CmsPage page, string fullPath)
{
    var normalized = fullPath.Trim('/');
    if (page.ParentPageId is null
        && (string.Equals(page.Slug, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(page.Slug, "index", StringComparison.OrdinalIgnoreCase)))
    {
        return "/";
    }

    return $"/{normalized}";
}

static bool IsCmsPagePubliclyVisible(CmsPage page, DateTimeOffset now) =>
    page.TrashedAt is null && PublicationWindows.IsVisible(page.Status, CmsPageStatuses.Published, page.PublishedAt, now);

static bool IsArticlePubliclyVisible(Article article, DateTimeOffset now) =>
    article.TrashedAt is null && PublicationWindows.IsVisible(article.Status, ArticleStatuses.Published, article.PublishedAt, now);

// Feeds the CMS builder's "posts-grid" widget (see CmsBlockHtmlRenderer.RenderPostsGrid) -
// a small, bounded, newest-first slice of published articles, computed once per request
// and reused by every posts-grid widget on the page regardless of each one's own "count".
static async Task<IReadOnlyList<PublicArticleSummary>> LoadPublicArticleSummariesAsync(
    IDbContextFactory<ApplicationDbContext> dbFactory, int take = 12)
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return await db.Articles
        .AsNoTracking()
        .Where(a => a.TrashedAt == null
            && a.Status == ArticleStatuses.Published
            && a.PublishedAtUnixSeconds != null
            && a.PublishedAtUnixSeconds <= nowUnixSeconds)
        .OrderByDescending(a => a.PublishedAtUnixSeconds)
        .Take(take)
        .Select(a => new PublicArticleSummary(
            a.Slug,
            a.Title,
            a.MetaDescription,
            a.HeroImageUrl ?? (a.HeroImageDataUri != "" ? $"/og-image/{a.Slug}" : null),
            a.PublishedAt))
        .ToListAsync();
}

static CommentView? FindCommentById(IEnumerable<CommentView> comments, Guid commentId)
{
    foreach (var comment in comments)
    {
        if (comment.Id == commentId)
        {
            return comment;
        }

        var reply = FindCommentById(comment.Replies, commentId);
        if (reply is not null)
        {
            return reply;
        }
    }

    return null;
}

// CSS isn't HTML-encoded before going into a <style> tag (encoding it would break the
// CSS), so the only injection vector worth closing is a literal "</style" that would let
// the custom CSS escape into raw HTML/script. This isn't a general HTML sanitizer.
static string SanitizeInlineCss(string css) =>
    css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);

// Article.Tags is a comma-separated free-form string (same convention as SecondaryKeywords /
// WatchedTopic.Keywords) - parsed on read rather than normalized into a join table.
static List<string> ParseTags(string tags) =>
    tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

// A misconfigured deploy that ships a blank/trivial AdminAuth:Password previously seeded
// it without any complaint — the admin login would then be guessable on day one. This is
// a floor (length + a denylist of obviously common values), not full entropy scoring.
static bool IsWeakSeedPassword(string password, string username, out string reason) =>
    GwsBusinessSuite.Application.Users.PasswordPolicy.IsWeak(password, username, out reason);

static async Task EnsureGrantWatsonHomepageAsync(ApplicationDbContext dbContext, IConfiguration configuration, ILogger logger)
{
    var siteName = configuration["Canvas:SiteName"] ?? "grantwatson.dev";
    var site = await dbContext.CmsSites.FirstOrDefaultAsync(s => s.Slug == GrantWatsonHomepageTemplate.SiteSlug);
    if (site is null)
    {
        site = new CmsSite
        {
            Name = siteName,
            Slug = GrantWatsonHomepageTemplate.SiteSlug,
            Theme = "Default",
            AccentColorHex = "#f59e0b",
            FontPairingKey = CmsFontPairings.Elegant,
            CreatedBy = "system"
        };
        dbContext.CmsSites.Add(site);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Created the default public CMS site for {SiteSlug}.", GrantWatsonHomepageTemplate.SiteSlug);
    }

    var homePage = await dbContext.CmsPages
        .FirstOrDefaultAsync(page => page.SiteId == site.Id && page.ParentPageId == null && page.Slug == "home");

    if (!GrantWatsonHomepageTemplate.ShouldApplyTemplate(homePage))
    {
        return;
    }

    var now = DateTimeOffset.UtcNow;
    var blocksJson = GrantWatsonHomepageTemplate.CreateBlocksJson();

    if (homePage is null)
    {
        homePage = new CmsPage
        {
            SiteId = site.Id,
            Title = "Home",
            Slug = "home",
            BlocksJson = blocksJson,
            MetaTitle = GrantWatsonHomepageTemplate.MetaTitle,
            MetaDescription = GrantWatsonHomepageTemplate.MetaDescription,
            Status = CmsPageStatuses.Published,
            PublishedAt = now,
            CreatedAt = now,
            CreatedBy = "system",
            UpdatedAt = now,
            UpdatedBy = "system"
        };
        dbContext.CmsPages.Add(homePage);
        logger.LogInformation("Created the public home page for {SiteSlug}.", GrantWatsonHomepageTemplate.SiteSlug);
    }
    else
    {
        homePage.Title = "Home";
        homePage.Slug = "home";
        homePage.ParentPageId = null;
        homePage.BlocksJson = blocksJson;
        homePage.MetaTitle = GrantWatsonHomepageTemplate.MetaTitle;
        homePage.MetaDescription = GrantWatsonHomepageTemplate.MetaDescription;
        homePage.Status = CmsPageStatuses.Published;
        homePage.PublishedAt ??= now;
        homePage.TrashedAt = null;
        homePage.UpdatedAt = now;
        homePage.UpdatedBy = "system";
        logger.LogInformation("Updated the legacy public home page for {SiteSlug}.", GrantWatsonHomepageTemplate.SiteSlug);
    }

    await dbContext.SaveChangesAsync();
}

// One-time content migration: folds the resume/CV content into the "about" Canvas page
// (see GrantWatsonAboutPageResumeSection) instead of it living on its own /resume page,
// and repoints the homepage's "See my Resumè" button at the new #resume anchor. Only
// touches what it's explicitly checking for — never re-applies the whole about-page
// layout — so any other edits made through the admin CMS builder UI are left alone.
static async Task EnsureAboutPageResumeSectionAsync(ApplicationDbContext dbContext, ILogger logger)
{
    var site = await dbContext.CmsSites.FirstOrDefaultAsync(s => s.Slug == GrantWatsonHomepageTemplate.SiteSlug);
    if (site is null)
    {
        return;
    }

    var now = DateTimeOffset.UtcNow;

    var homePage = await dbContext.CmsPages
        .FirstOrDefaultAsync(page => page.SiteId == site.Id && page.ParentPageId == null && page.Slug == "home");
    var homeLayout = homePage is null ? null : CmsBuilderJson.ParseLayout(homePage.BlocksJson);
    var resumeButton = homeLayout?.Sections
        .SelectMany(section => section.Columns)
        .SelectMany(column => column.Widgets)
        .FirstOrDefault(widget =>
            widget.WidgetType == "button"
            && widget.Props.TryGetValue("href", out var href)
            && (href == "/resume" || href == "/about#resume"));
    if (resumeButton is not null && homePage is not null)
    {
        var hrefNeedsFix = resumeButton.Props["href"] != "/about#resume";
        var opensInNewTab = resumeButton.Props.TryGetValue("openInNewTab", out var openInNewTab) && openInNewTab == "true";
        if (hrefNeedsFix || opensInNewTab)
        {
            resumeButton.Props["href"] = "/about#resume";
            // It's now an in-page anchor, not a separate page, so it shouldn't pop a new tab.
            resumeButton.Props["openInNewTab"] = "false";
            homePage.BlocksJson = CmsBuilderJson.Serialize(homeLayout!);
            homePage.UpdatedAt = now;
            homePage.UpdatedBy = "system";
            logger.LogInformation("Repointed the homepage's resume button at /about#resume (same tab).");
        }
    }

    var aboutPage = await dbContext.CmsPages
        .FirstOrDefaultAsync(page => page.SiteId == site.Id && page.Slug == "about");
    if (aboutPage is not null)
    {
        var aboutLayout = CmsBuilderJson.ParseLayout(aboutPage.BlocksJson);
        if (!GrantWatsonAboutPageResumeSection.HasResumeSection(aboutLayout))
        {
            aboutLayout ??= new PageLayout();
            aboutLayout.Sections.Add(GrantWatsonAboutPageResumeSection.Build());
            aboutPage.BlocksJson = CmsBuilderJson.Serialize(aboutLayout);
            aboutPage.UpdatedAt = now;
            aboutPage.UpdatedBy = "system";
            logger.LogInformation("Added the resume section to the public about page.");
        }
    }

    await dbContext.SaveChangesAsync();
}

// These mutation endpoints are authenticated, but cookie auth alone gives no CSRF
// protection for non-browser-form requests. Callers must fetch a token from
// GET /admin/api/antiforgery-token first and send it back via the X-CSRF-TOKEN header.
static async Task<IResult?> ValidateAntiforgeryAsync(HttpContext httpContext, IAntiforgery antiforgery)
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        return null;
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Missing or invalid CSRF token." });
    }
}

static string NormalizePathBase(string? pathBase)
{
    if (string.IsNullOrWhiteSpace(pathBase))
    {
        return string.Empty;
    }

    var normalized = pathBase.Trim();
    if (!normalized.StartsWith('/'))
    {
        normalized = $"/{normalized}";
    }

    return normalized.Length > 1
        ? normalized.TrimEnd('/')
        : normalized;
}

static bool IsSafeLocalPath(string? returnUrl)
{
    return !string.IsNullOrWhiteSpace(returnUrl)
           && returnUrl.StartsWith("/", StringComparison.Ordinal)
           && !returnUrl.StartsWith("//", StringComparison.Ordinal)
           && !returnUrl.Contains("\\", StringComparison.Ordinal);
}

record ArticleUpsertRequest(
    string Title,
    string Slug,
    string? Topic,
    string Author,
    string PrimaryKeyword,
    string SecondaryKeywords,
    string MetaDescription,
    string EstimatedReadingTime,
    string? HeroImageUrl,
    string HeroImageAltText,
    string HeroImageCaption,
    string BodyMarkdown);

// Bundles both named theme locations (Primary/header, Footer) so callers that render a full
// page shell only need one site lookup instead of two.
sealed record PublicNavMenus(
    IReadOnlyList<NavMenuItem> Primary,
    IReadOnlyList<NavMenuItem> Footer,
    string? AccentColorHex,
    string? FontPairingKey,
    string? SiteName,
    string? LogoUrl,
    string? FaviconUrl);
