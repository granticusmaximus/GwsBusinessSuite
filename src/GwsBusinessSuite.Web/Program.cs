using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser().RequireRole("Admin"));

    options.FallbackPolicy = options.GetPolicy("AdminOnly");
});

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
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Redirect("/admin/login?error=ratelimit");
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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

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

app.MapPost("/auth/login", async (HttpContext httpContext, IAntiforgery antiforgery, IConfiguration configuration) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.LocalRedirect("/admin/login?error=invalid");
    }

    var form = await httpContext.Request.ReadFormAsync();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var configuredUsername = configuration["AdminAuth:Username"]?.Trim();
    var configuredPassword = configuration["AdminAuth:Password"];

    if (string.IsNullOrWhiteSpace(configuredUsername) || string.IsNullOrWhiteSpace(configuredPassword))
    {
        return Results.LocalRedirect("/admin/login?error=missing");
    }

    if (!string.Equals(username, configuredUsername, StringComparison.Ordinal) ||
        !string.Equals(password, configuredPassword, StringComparison.Ordinal))
    {
        var safeReturnUrl = IsSafeLocalPath(returnUrl) ? returnUrl : "/admin";
        return Results.LocalRedirect($"/admin/login?error=invalid&returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, configuredUsername),
        new(ClaimTypes.Role, "Admin")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

    return Results.LocalRedirect(IsSafeLocalPath(returnUrl) ? returnUrl : "/admin");
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
}).AllowAnonymous();

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
}).AllowAnonymous();

app.MapGet("/api/blog/{slug}", async (
    string slug,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var a = await db.Articles
        .Where(x => x.Slug == slug && x.Status == ArticleStatuses.Published)
        .FirstOrDefaultAsync();

    if (a is null) return Results.NotFound();

    var heroImageUrl = a.HeroImageUrl
        ?? (a.HeroImageDataUri != "" ? $"/og-image/{a.Slug}" : null);

    return Results.Ok(new
    {
        a.Slug,
        a.Title,
        a.Topic,
        a.MetaDescription,
        ArticleMarkdown      = a.BodyMarkdown,
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
}).AllowAnonymous();

// Admin: publish an approved SeoArticleDraft to the live blog as an Article.
app.MapPost("/admin/api/articles/publish-draft/{draftId:guid}", async (
    Guid draftId,
    IContentStudioService contentStudioService) =>
{
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
app.MapPost("/admin/api/articles", async (ArticleUpsertRequest req, IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
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
        CreatedBy         = "manual-editor"
    };
    db.Articles.Add(article);
    await db.SaveChangesAsync();
    return Results.Created($"/admin/api/articles/{article.Id}", new { article.Id, article.Slug });
}).RequireAuthorization();

// Admin: update an existing article
app.MapPut("/admin/api/articles/{id:guid}", async (
    Guid id,
    ArticleUpsertRequest req,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
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
    article.UpdatedBy         = "manual-editor";
    article.UpdatedAt         = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status });
}).RequireAuthorization();

// Admin: publish an article (sets Status=Published, stamps PublishedAt if not already set)
app.MapPost("/admin/api/articles/{id:guid}/publish", async (Guid id, IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Status      = ArticleStatuses.Published;
    article.PublishedAt ??= DateTimeOffset.UtcNow;
    article.UpdatedAt   = DateTimeOffset.UtcNow;
    article.UpdatedBy   = "manual-editor";
    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status, article.PublishedAt });
}).RequireAuthorization();

// Admin: unpublish an article (back to Draft)
app.MapPost("/admin/api/articles/{id:guid}/unpublish", async (Guid id, IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();

    article.Status    = ArticleStatuses.Draft;
    article.UpdatedAt = DateTimeOffset.UtcNow;
    article.UpdatedBy = "manual-editor";
    await db.SaveChangesAsync();
    return Results.Ok(new { article.Id, article.Slug, article.Status });
}).RequireAuthorization();

// Admin: delete a draft article (published articles must be unpublished first)
app.MapDelete("/admin/api/articles/{id:guid}", async (Guid id, IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var article = await db.Articles.FindAsync(id);
    if (article is null) return Results.NotFound();
    if (article.Status == ArticleStatuses.Published)
        return Results.BadRequest(new { error = "Unpublish the article before deleting it." });

    db.Articles.Remove(article);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Root redirect to admin for anyone who hits the backend URL directly.
app.MapGet("/", () => Results.Redirect("/admin")).AllowAnonymous();

app.Run();

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
