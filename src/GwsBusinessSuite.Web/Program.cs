using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

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

// Inject Open Graph / Twitter Card meta tags for /blog/{slug} requests.
// Crawlers (social platforms, search engines) hit this before JS runs, so we
// bake the tags into the HTML server-side and let React hydrate normally.
app.Use(async (context, next) =>
{
    var pathValue = context.Request.Path.Value ?? "";
    var blogSlugMatch = System.Text.RegularExpressions.Regex.Match(
        pathValue, @"^/blog/([^/]+)/?$");

    if (!blogSlugMatch.Success)
    {
        await next();
        return;
    }

    var slug = blogSlugMatch.Groups[1].Value;
    var dbFactory = context.RequestServices
        .GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    var article = await db.SeoArticleDrafts
        .Where(a => a.Slug == slug)
        .Select(a => new
        {
            a.Title, a.Topic, a.Slug, a.MetaDescription,
            a.PrimaryKeyword, a.SecondaryKeywords,
            a.ArticleMarkdown, a.HeroImageDataUri,
            a.HeroImageAltText, a.HeroImageCaption,
            a.EstimatedReadingTime, a.ApprovedAt, a.CreatedAt
        })
        .FirstOrDefaultAsync();

    var indexPath = Path.Combine(app.Environment.WebRootPath, "index.html");

    if (article is null || !File.Exists(indexPath))
    {
        await next();
        return;
    }

    var html = await File.ReadAllTextAsync(indexPath);

    var title = !string.IsNullOrWhiteSpace(article.Title) ? article.Title : article.Topic;
    var description = !string.IsNullOrWhiteSpace(article.MetaDescription)
        ? article.MetaDescription
        : StripMarkdownExcerpt(article.ArticleMarkdown, 160);
    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    var canonicalUrl = $"{baseUrl}/blog/{article.Slug}";
    var hasImage = !string.IsNullOrWhiteSpace(article.HeroImageDataUri);
    var imageUrl = hasImage ? $"{baseUrl}/og-image/{article.Slug}" : null;
    var imageAlt = !string.IsNullOrWhiteSpace(article.HeroImageAltText)
        ? article.HeroImageAltText : title;
    var keywords = string.Join(", ",
        new[] { article.PrimaryKeyword, article.SecondaryKeywords }
        .Where(k => !string.IsNullOrWhiteSpace(k)));
    var publishedTime = (article.ApprovedAt ?? article.CreatedAt).ToString("o");

    var ogBlock = BuildOgMetaBlock(
        title, description, canonicalUrl, imageUrl, imageAlt,
        keywords, publishedTime);

    // Swap <title> and inject OG block before </head>
    html = System.Text.RegularExpressions.Regex.Replace(
        html, @"<title>[^<]*</title>",
        $"<title>{HtmlEncode(title)}</title>");
    html = html.Replace("</head>", $"{ogBlock}\n</head>",
        StringComparison.OrdinalIgnoreCase);

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
});

// Rewrite public (non-admin) HTML navigation requests to React's index.html.
// Static file requests (any path with a file extension) pass through unchanged.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isAdmin = path.StartsWithSegments("/admin");
    var isApi = path.StartsWithSegments("/auth")
             || path.StartsWithSegments("/api")
             || path.StartsWithSegments("/og-image")
             || path.StartsWithSegments("/cms-preview")
             || path.StartsWithSegments("/_blazor")
             || path.StartsWithSegments("/_framework");
    var isFile = path.Value?.Contains('.') == true;

    if (!isAdmin && !isApi && !isFile)
        context.Request.Path = "/index.html";

    await next();
});

// Serve React static files (wwwroot/index.html, wwwroot/assets/*, etc.) before auth checks.
app.UseStaticFiles();

if (!string.IsNullOrWhiteSpace(normalizedPathBase))
{
    app.UsePathBase(normalizedPathBase);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

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

app.MapPost("/auth/login", async (HttpContext httpContext, IConfiguration configuration) =>
{
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
}).AllowAnonymous();

app.MapGet("/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/admin/login");
}).AllowAnonymous();

// Serve hero images stored as base64 data URIs as real image responses
// so og:image tags have a cacheable URL that social crawlers can fetch.
app.MapGet("/og-image/{slug}", async (
    string slug,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var row = await db.SeoArticleDrafts
        .Where(a => a.Slug == slug && a.HeroImageDataUri != "")
        .Select(a => new { a.HeroImageDataUri })
        .FirstOrDefaultAsync();

    if (row is null || string.IsNullOrWhiteSpace(row.HeroImageDataUri))
        return Results.NotFound();

    var commaIdx = row.HeroImageDataUri.IndexOf(',');
    if (commaIdx < 0) return Results.NotFound();

    var header = row.HeroImageDataUri[..commaIdx];          // "data:image/png;base64"
    var base64  = row.HeroImageDataUri[(commaIdx + 1)..];

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

// Public JSON API — returns article metadata + markdown for the React blog view.
app.MapGet("/api/blog", async (IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    // Fetch columns first; sort DateTimeOffset in memory (SQLite limitation).
    var rows = await db.SeoArticleDrafts
        .Select(a => new
        {
            a.Id, a.Slug,
            Title        = a.Title != "" ? a.Title : a.Topic,
            a.MetaDescription, a.EstimatedReadingTime,
            a.PrimaryKeyword, a.Status,
            HasHeroImage = a.HeroImageDataUri != "",
            a.ApprovedAt, a.CreatedAt
        })
        .ToListAsync();

    var articles = rows
        .OrderByDescending(a => a.ApprovedAt ?? a.CreatedAt)
        .Select(a => new
        {
            a.Id, a.Slug, a.Title, a.MetaDescription,
            a.EstimatedReadingTime, a.PrimaryKeyword,
            a.Status, a.HasHeroImage,
            PublishedAt = a.ApprovedAt ?? a.CreatedAt
        });

    return Results.Ok(articles);
}).AllowAnonymous();

app.MapGet("/api/blog/{slug}", async (
    string slug,
    IDbContextFactory<ApplicationDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var a = await db.SeoArticleDrafts
        .Where(x => x.Slug == slug)
        .FirstOrDefaultAsync();

    if (a is null) return Results.NotFound();

    return Results.Ok(new
    {
        a.Id,
        a.Slug,
        Title              = a.Title != "" ? a.Title : a.Topic,
        a.Topic,
        a.MetaDescription,
        a.ArticleMarkdown,
        a.EstimatedReadingTime,
        a.PrimaryKeyword,
        a.SecondaryKeywords,
        a.HeroImageAltText,
        a.HeroImageCaption,
        a.Status,
        Author             = "Grant Watson",
        HasHeroImage       = a.HeroImageDataUri != "",
        PublishedAt        = a.ApprovedAt ?? a.CreatedAt
    });
}).AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Safety net: serve React index.html for any route that Blazor didn't handle.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

static string StripMarkdownExcerpt(string markdown, int maxLength)
{
    if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

    var candidate = markdown
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim())
        .FirstOrDefault(l => !l.StartsWith('#')
                          && !l.StartsWith("```")
                          && !l.StartsWith(">")
                          && l.Length > 20);

    if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;

    // Strip inline markdown: bold, italic, code, links
    candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[*_`~]", "");
    candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"\[([^\]]+)\]\([^)]+\)", "$1");

    return candidate.Length > maxLength
        ? candidate[..maxLength].TrimEnd() + "…"
        : candidate;
}

static string HtmlEncode(string text) =>
    text.Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

static string BuildOgMetaBlock(
    string title, string description, string canonicalUrl,
    string? imageUrl, string imageAlt, string keywords, string publishedTime)
{
    var sb = new StringBuilder();

    sb.AppendLine($"  <link rel=\"canonical\" href=\"{canonicalUrl}\" />");

    // Standard SEO
    if (!string.IsNullOrWhiteSpace(description))
        sb.AppendLine($"  <meta name=\"description\" content=\"{HtmlEncode(description)}\" />");
    if (!string.IsNullOrWhiteSpace(keywords))
        sb.AppendLine($"  <meta name=\"keywords\" content=\"{HtmlEncode(keywords)}\" />");
    sb.AppendLine("  <meta name=\"author\" content=\"Grant Watson\" />");

    // Open Graph
    sb.AppendLine("  <meta property=\"og:type\" content=\"article\" />");
    sb.AppendLine($"  <meta property=\"og:site_name\" content=\"Grant Watson\" />");
    sb.AppendLine($"  <meta property=\"og:url\" content=\"{canonicalUrl}\" />");
    sb.AppendLine($"  <meta property=\"og:title\" content=\"{HtmlEncode(title)}\" />");
    if (!string.IsNullOrWhiteSpace(description))
        sb.AppendLine($"  <meta property=\"og:description\" content=\"{HtmlEncode(description)}\" />");
    if (imageUrl is not null)
    {
        sb.AppendLine($"  <meta property=\"og:image\" content=\"{imageUrl}\" />");
        sb.AppendLine($"  <meta property=\"og:image:alt\" content=\"{HtmlEncode(imageAlt)}\" />");
    }
    sb.AppendLine($"  <meta property=\"article:published_time\" content=\"{publishedTime}\" />");
    sb.AppendLine("  <meta property=\"article:author\" content=\"Grant Watson\" />");

    // Twitter / X
    sb.AppendLine($"  <meta name=\"twitter:card\" content=\"{(imageUrl is not null ? "summary_large_image" : "summary")}\" />");
    sb.AppendLine($"  <meta name=\"twitter:title\" content=\"{HtmlEncode(title)}\" />");
    if (!string.IsNullOrWhiteSpace(description))
        sb.AppendLine($"  <meta name=\"twitter:description\" content=\"{HtmlEncode(description)}\" />");
    if (imageUrl is not null)
    {
        sb.AppendLine($"  <meta name=\"twitter:image\" content=\"{imageUrl}\" />");
        sb.AppendLine($"  <meta name=\"twitter:image:alt\" content=\"{HtmlEncode(imageAlt)}\" />");
    }

    return sb.ToString().TrimEnd();
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
