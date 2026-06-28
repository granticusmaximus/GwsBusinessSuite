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

app.MapGet("/cms-preview/{pageKey}", async (string pageKey, IReactPageBuilderService builderService) =>
{
    var state = await builderService.LoadEditorStateAsync(pageKey);
    if (state is null)
        return Results.NotFound();

    var css = new StringBuilder();
    foreach (var uiFile in state.UiFiles)
    {
        if (uiFile.FileType is "css" or "scss" or "sass" or "less")
        {
            try
            {
                var content = await builderService.ReadFileContentAsync(uiFile.FilePath);
                css.AppendLine($"/* {uiFile.FileName} */");
                css.AppendLine(content);
            }
            catch { /* skip unreadable files */ }
        }
    }

    var jsxSource = string.Empty;
    if (!string.IsNullOrWhiteSpace(state.FilePath))
    {
        try { jsxSource = await builderService.ReadFileContentAsync(state.FilePath); }
        catch { /* no source available */ }
    }

    var html = BuildCmsPreviewHtml(pageKey, css.ToString(), PreprocessJsxForPreview(jsxSource));
    return Results.Content(html, "text/html");
}).RequireAuthorization();

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

// Serves a CmsSite/CmsPage built in the structured CMS Builder ("/admin/cms-builder") as a
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

    var bodyHtml = CmsBlockHtmlRenderer.Render(page.BlocksJson);
    var pageTitle = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle);
    var metaDescription = System.Net.WebUtility.HtmlEncode(page.MetaDescription);
    var ogImageTag = string.IsNullOrWhiteSpace(page.OgImageUrl)
        ? string.Empty
        : $"""<meta property="og:image" content="{System.Net.WebUtility.HtmlEncode(page.OgImageUrl)}" />""";

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
        </head>
        <body>
          {bodyHtml}
        </body>
        </html>
        """;

    return Results.Content(html, "text/html");
}).AllowAnonymous().RequireRateLimiting("public-read");

// Decodes and serves a base64-stored media library asset, mirroring the /og-image pattern.
app.MapGet("/media/{id:guid}", async (Guid id, IMediaLibraryService mediaLibraryService) =>
{
    var content = await mediaLibraryService.GetContentAsync(id);
    return content is null ? Results.NotFound() : Results.Bytes(content.Value.Content, content.Value.ContentType);
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

static string PreprocessJsxForPreview(string source)
{
    if (string.IsNullOrWhiteSpace(source))
        return "function __GWSPage() { return React.createElement('div', null, 'No source available'); }";

    var result = source;

    // React default import (with optional named imports): import React, { useState } from 'react';
    result = Regex.Replace(result,
        @"import\s+React\s*(?:,\s*\{([^}]*)\}\s*)?from\s+['""]react['""];?\r?\n?",
        m =>
        {
            var named = m.Groups[1].Value.Trim();
            return string.IsNullOrEmpty(named) ? "" : $"const {{ {named} }} = React;\n";
        }, RegexOptions.Multiline);

    // ReactDOM imports
    result = Regex.Replace(result,
        @"import\s+(?:\w+|\{[^}]*\})\s+from\s+['""]react-dom(?:/[^'""]*)?['""];?\r?\n?",
        "", RegexOptions.Multiline);

    // React named-only import: import { useState } from 'react';
    result = Regex.Replace(result,
        @"import\s+\{([^}]*)\}\s+from\s+['""]react['""];?\r?\n?",
        m => $"const {{ {m.Groups[1].Value.Trim()} }} = React;\n",
        RegexOptions.Multiline);

    // CSS module imports: import styles from './X.module.css';
    result = Regex.Replace(result,
        @"import\s+(\w+)\s+from\s+['""][^'""]*\.module\.[^'""]+['""];?\r?\n?",
        m => $"const {m.Groups[1].Value} = new Proxy({{}}, {{ get: (_, k) => k }});\n",
        RegexOptions.Multiline);

    // CSS side-effect imports: import './App.css';
    result = Regex.Replace(result,
        @"import\s+['""][^'""]*\.(?:css|scss|sass|less)['""];?\r?\n?",
        "", RegexOptions.Multiline);

    // Namespace imports: import * as X from './path';
    result = Regex.Replace(result,
        @"import\s+\*\s+as\s+(\w+)\s+from\s+['""][^'""]*['""];?\r?\n?",
        m => $"const {m.Groups[1].Value} = {{}};\n",
        RegexOptions.Multiline);

    // Named imports from any path: import { X, Y } from './path';
    result = Regex.Replace(result,
        @"import\s+\{([^}]*)\}\s+from\s+['""][^'""]*['""];?\r?\n?",
        m =>
        {
            var names = m.Groups[1].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Contains(" as ") ? n.Split(" as ", StringSplitOptions.TrimEntries)[1] : n.Trim());
            return $"const {{ {string.Join(", ", names)} }} = {{}};\n";
        }, RegexOptions.Multiline);

    // Default imports: import Something from './path';
    result = Regex.Replace(result,
        @"import\s+(\w+)\s+from\s+['""][^'""]*['""];?\r?\n?",
        m => $"const {m.Groups[1].Value} = () => null;\n",
        RegexOptions.Multiline);

    // Bare imports: import 'something';
    result = Regex.Replace(result,
        @"import\s+['""][^'""]*['""];?\r?\n?",
        "", RegexOptions.Multiline);

    // export default function ComponentName( → function __GWSPage(
    result = Regex.Replace(result,
        @"export\s+default\s+function\s+\w+\s*\(",
        "function __GWSPage(", RegexOptions.Multiline);

    // export default function( (anonymous)
    result = Regex.Replace(result,
        @"export\s+default\s+function\s*\(",
        "function __GWSPage(", RegexOptions.Multiline);

    // export default class ClassName
    result = Regex.Replace(result,
        @"export\s+default\s+class\s+\w+",
        "class __GWSPage", RegexOptions.Multiline);

    // export default ComponentName; (standalone trailing export)
    result = Regex.Replace(result,
        @"^export\s+default\s+(\w+)\s*;?\s*$",
        "const __GWSPage = $1;", RegexOptions.Multiline);

    // export { X as default };
    result = Regex.Replace(result,
        @"^export\s+\{[^}]*\b(\w+)\s+as\s+default[^}]*\}\s*;?\s*$",
        "const __GWSPage = $1;", RegexOptions.Multiline);

    // export const → const
    result = Regex.Replace(result, @"^export\s+const\b", "const", RegexOptions.Multiline);

    // export function → function
    result = Regex.Replace(result, @"^export\s+function\b", "function", RegexOptions.Multiline);

    // export { X, Y }; (named-only exports — strip)
    result = Regex.Replace(result, @"^export\s+\{[^}]*\}\s*;?\s*$", "", RegexOptions.Multiline);

    return result.Trim();
}

static string BuildCmsPreviewHtml(string pageKey, string inlineCss, string processedJsx)
{
    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE html>");
    sb.AppendLine("<html lang=\"en\">");
    sb.AppendLine("<head>");
    sb.AppendLine("  <meta charset=\"UTF-8\">");
    sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
    sb.AppendLine($"  <title>Preview \u2013 {pageKey}</title>");
    sb.AppendLine("  <style>");
    sb.AppendLine("    *, *::before, *::after { box-sizing: border-box; }");
    sb.AppendLine("    body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif; }");
    sb.AppendLine("    img { max-width: 100%; height: auto; display: block; }");
    sb.AppendLine("  </style>");
    sb.AppendLine($"  <style id=\"page-css\">{inlineCss}</style>");
    sb.AppendLine("  <script crossorigin src=\"https://unpkg.com/react@18/umd/react.development.js\"></script>");
    sb.AppendLine("  <script crossorigin src=\"https://unpkg.com/react-dom@18/umd/react-dom.development.js\"></script>");
    sb.AppendLine("  <script src=\"https://unpkg.com/@babel/standalone/babel.min.js\"></script>");
    sb.AppendLine("</head>");
    sb.AppendLine("<body>");
    sb.AppendLine("  <div id=\"root\"></div>");
    sb.AppendLine("  <script type=\"text/babel\" data-presets=\"react\">");
    sb.AppendLine(processedJsx);
    sb.AppendLine(";(function() {");
    sb.AppendLine("  var _gwsEl = typeof __GWSPage !== 'undefined' ? __GWSPage");
    sb.AppendLine("    : function() {");
    sb.AppendLine("        return React.createElement('div', { style: { fontFamily: 'sans-serif', padding: '3rem', textAlign: 'center', color: '#64748b' } },");
    sb.AppendLine($"          React.createElement('h3', {{ style: {{ fontWeight: 600, marginBottom: '0.5rem' }} }}, 'Preview \u2013 {pageKey}'),");
    sb.AppendLine("          React.createElement('p', { style: { margin: 0 } }, 'Component export not resolved. Save the file to refresh.'));");
    sb.AppendLine("      };");
    sb.AppendLine("  ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(_gwsEl));");
    sb.AppendLine("})();");
    sb.AppendLine("  </script>");
    sb.AppendLine("</body>");
    sb.Append("</html>");
    return sb.ToString();
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
