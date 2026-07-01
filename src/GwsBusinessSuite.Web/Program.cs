using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;


var builder = WebApplication.CreateBuilder(args);
var configuredPathBase = builder.Configuration["Hosting:PathBase"];

// Add services to the container.
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

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

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var dbContext = await dbFactory.CreateDbContextAsync();
    await dbContext.Database.MigrateAsync();

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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/admin/not-found", createScopeForStatusCodePages: true);
// HTTP-only in Docker — remove HTTPS redirect so the container runs cleanly on port 8080

// Serve Blazor static assets before auth checks.
app.UseStaticFiles();

if (!string.IsNullOrWhiteSpace(normalizedPathBase))
{
    app.UsePathBase(normalizedPathBase);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

// Plain-text Healthy/Degraded/Unhealthy with no detail disclosure, intended for the deploy
// pipeline's post-deploy smoke test and any external uptime monitor.
app.MapHealthChecks("/health").AllowAnonymous();

app.MapPost("/auth/login", async (
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPasswordHasher<AppUser> hasher) =>
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

    await using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.AppUsers
        .FirstOrDefaultAsync(u => u.Username == username && u.IsActive);

    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
    {
        var safeReturn = IsSafeLocalPath(returnUrl) ? returnUrl : "/admin";
        return Results.LocalRedirect($"/admin/login?error=invalid&returnUrl={Uri.EscapeDataString(safeReturn)}");
    }

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
}).AllowAnonymous().RequireRateLimiting("public-read");

// Serves a CmsSite/CmsPage built in Canvas ("/admin/canvas") as a
// standalone public HTML page. These pages are independent of the apps/public-site React
// app — the structured builder targets sites that don't have a hand-written frontend.
app.MapGet("/cms/{siteSlug}/{pageSlug}", async (
    string siteSlug,
    string pageSlug,
    ICmsBuilderService cmsBuilderService) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var page = await cmsBuilderService.GetPageBySlugAsync(site.Id, pageSlug);
    if (page is null) return Results.NotFound();

    var bodyHtml = CmsBlockHtmlRenderer.Render(page.BlocksJson, siteSlug, pageSlug);
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
        </body>
        </html>
        """;

    return Results.Content(html, "text/html");
}).AllowAnonymous().RequireRateLimiting("public-read");

// Handles "form" widget submissions (see CmsBlockHtmlRenderer's "form" case).
// Tightly rate-limited since it's an unauthenticated POST that writes to the database —
// a much smaller budget than ordinary page views.
app.MapPost("/cms/{siteSlug}/{pageSlug}/submit", async (
    string siteSlug,
    string pageSlug,
    HttpRequest request,
    ICmsBuilderService cmsBuilderService,
    IFormSubmissionService formSubmissionService) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var page = await cmsBuilderService.GetPageBySlugAsync(site.Id, pageSlug);
    if (page is null) return Results.NotFound();

    var form = await request.ReadFormAsync();

    // Honeypot: a hidden field real visitors never see or fill. A non-empty value means a
    // bot filled every field it found — accept silently so the bot doesn't learn it failed.
    if (!string.IsNullOrWhiteSpace(form["company"]))
    {
        return Results.Redirect($"/cms/{siteSlug}/{pageSlug}/thanks");
    }

    // The form widget's fields are admin-defined per page, so collect whatever was
    // actually posted (minus the honeypot) rather than assuming fixed field names.
    var fields = form
        .Where(kvp => kvp.Key != "company")
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

    try
    {
        await formSubmissionService.SubmitAsync(page.Id, fields);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Redirect($"/cms/{siteSlug}/{pageSlug}/thanks");
}).AllowAnonymous().RequireRateLimiting("public-write");

app.MapGet("/cms/{siteSlug}/{pageSlug}/thanks", (string siteSlug, string pageSlug) =>
{
    var html = $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Thank you</title>
          <link rel="stylesheet" href="/cms-public.css" />
        </head>
        <body>
          <section class="cms-block">
            <div class="cms-callout">
              <h2>Thanks — your message was sent.</h2>
              <p>We'll get back to you soon.</p>
              <a class="cms-button" href="/cms/{System.Net.WebUtility.HtmlEncode(siteSlug)}/{System.Net.WebUtility.HtmlEncode(pageSlug)}">Back</a>
            </div>
          </section>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html");
}).AllowAnonymous().RequireRateLimiting("public-read");

// Admin: exports a full CmsSite as a self-contained static ZIP so the output can be
// deployed to any host that serves static files. Each page becomes its own HTML file
// with all CSS inlined (no external links), making the archive fully portable.
app.MapGet("/admin/api/cms/{siteSlug}/export.zip", async (
    string siteSlug,
    ICmsBuilderService cmsBuilderService,
    IWebHostEnvironment env) =>
{
    var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
    if (site is null) return Results.NotFound();

    var pages = await cmsBuilderService.ListPagesAsync(site.Id);

    // Read the base stylesheet once — it gets embedded in every page.
    var cssFilePath = Path.Combine(env.WebRootPath, "cms-public.css");
    var baseStylesheet = File.Exists(cssFilePath) ? await File.ReadAllTextAsync(cssFilePath) : string.Empty;

    await using var memoryStream = new MemoryStream();
    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var page in pages)
        {
            var bodySections = CmsBlockHtmlRenderer.Render(page.BlocksJson, site.Slug, page.Slug);
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

            // Each page lives at {slug}/index.html so /about/ resolves correctly on any
            // static host, and the site root gets an index.html that lists all pages.
            var entryPath = string.Equals(page.Slug, "index", StringComparison.OrdinalIgnoreCase)
                ? "index.html"
                : $"{page.Slug}/index.html";

            var entry = archive.CreateEntry(entryPath, CompressionLevel.SmallestSize);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(pageHtml);
        }

        // Top-level index.html if no page has a slug of "index"
        if (!pages.Any(p => string.Equals(p.Slug, "index", StringComparison.OrdinalIgnoreCase)))
        {
            var pageLinks = string.Concat(pages.Select(p =>
                $"""<li><a href="./{System.Net.WebUtility.HtmlEncode(p.Slug)}/">{System.Net.WebUtility.HtmlEncode(p.Title)}</a></li>"""));

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

    var pages = await cmsBuilderService.ListPagesAsync(site.Id);

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
    if (page is null) return Results.NotFound();

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
    var articles = await db.Articles
        .AsNoTracking()
        .Where(a => a.Status == ArticleStatuses.Published)
        .ToListAsync();

    var result = articles
        .OrderByDescending(a => a.PublishedAt)
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
        .Where(x => x.Slug == slug && x.Status == ArticleStatuses.Published)
        .FirstOrDefaultAsync();

    if (a is null) return Results.NotFound();

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
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

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
        CreatedBy         = GetCurrentUsername(httpContext)
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
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

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
    article.UpdatedBy         = GetCurrentUsername(httpContext);
    article.UpdatedAt         = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status });
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: publish an article (sets Status=Published, stamps PublishedAt if not already set)
app.MapPost("/admin/api/articles/{id:guid}/publish", async (
    Guid id,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Status      = ArticleStatuses.Published;
    article.PublishedAt ??= DateTimeOffset.UtcNow;
    article.UpdatedAt   = DateTimeOffset.UtcNow;
    article.UpdatedBy   = GetCurrentUsername(httpContext);
    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status, article.PublishedAt });
}).RequireAuthorization().RequireRateLimiting("admin-mutation");

// Admin: unpublish an article (back to Draft)
app.MapPost("/admin/api/articles/{id:guid}/unpublish", async (
    Guid id,
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } rejection) return rejection;

    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Status    = ArticleStatuses.Draft;
    article.UpdatedAt = DateTimeOffset.UtcNow;
    article.UpdatedBy = GetCurrentUsername(httpContext);
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

// Root redirect to admin for anyone who hits the backend URL directly.
app.MapGet("/", () => Results.Redirect("/admin")).AllowAnonymous();

app.Run();

// CSS isn't HTML-encoded before going into a <style> tag (encoding it would break the
// CSS), so the only injection vector worth closing is a literal "</style" that would let
// the custom CSS escape into raw HTML/script. This isn't a general HTML sanitizer.
static string SanitizeInlineCss(string css) =>
    css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);

// A misconfigured deploy that ships a blank/trivial AdminAuth:Password previously seeded
// it without any complaint — the admin login would then be guessable on day one. This is
// a floor (length + a denylist of obviously common values), not full entropy scoring.
static bool IsWeakSeedPassword(string password, string username, out string reason)
{
    string[] commonWeakSeedPasswords =
    [
        "password", "admin", "administrator", "changeme", "letmein",
        "12345678", "123456789", "qwertyuiop", "password123"
    ];

    if (password.Length < 12)
    {
        reason = "is shorter than the required 12 characters";
        return true;
    }

    if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
    {
        reason = "must not be the same as the username";
        return true;
    }

    if (commonWeakSeedPasswords.Contains(password, StringComparer.OrdinalIgnoreCase))
    {
        reason = "is a commonly guessed password";
        return true;
    }

    reason = string.Empty;
    return false;
}

static string GetCurrentUsername(HttpContext httpContext)
{
    var username = httpContext.User.Identity?.Name;
    return string.IsNullOrWhiteSpace(username) ? "unknown" : username;
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
