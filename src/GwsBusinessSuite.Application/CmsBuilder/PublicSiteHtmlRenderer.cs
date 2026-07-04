using System.Net;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Domain.Entities;
using Markdig;

namespace GwsBusinessSuite.Application.CmsBuilder;

/// <summary>
/// Renders the shared page shell (head/nav/footer), blog list/post markup, and the public
/// 404 for grantwatson.dev — the server-rendered replacement for the retired
/// apps/public-site React app. Ported class-for-class from that app's App.css-driven markup
/// (Navbar/Footer in App.jsx, ArticleCard.jsx, BlogList.jsx, BlogPost.jsx) so
/// wwwroot/public-site.css didn't need to change shape. Canvas page bodies (Home/About/
/// Contact/etc.) are still rendered by <see cref="CmsBlockHtmlRenderer"/> and just get
/// wrapped in <see cref="Layout"/> here.
/// </summary>
public static class PublicSiteHtmlRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string RenderMarkdown(string markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? string.Empty : Markdown.ToHtml(markdown, MarkdownPipeline);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Falls back to the original hardcoded About/Blog/Contact list when a site has no
    // NavMenuJson set yet, so a freshly-migrated site never renders a blank nav.
    private static readonly IReadOnlyList<NavMenuItem> DefaultNavItems =
    [
        new("about", "About", "/about", false),
        new("blog", "Blog", "/blog", false),
        new("contact", "Contact", "/contact", false)
    ];

    public static IReadOnlyList<NavMenuItem> ParseNavItems(string? navMenuJson)
    {
        if (string.IsNullOrWhiteSpace(navMenuJson))
        {
            return DefaultNavItems;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<NavMenuItem>>(navMenuJson, JsonOptions);
            return items is { Count: > 0 } ? items : DefaultNavItems;
        }
        catch (JsonException)
        {
            return DefaultNavItems;
        }
    }

    public sealed record ArticleSummary(
        string Slug,
        string Title,
        string MetaDescription,
        string PrimaryKeyword,
        string EstimatedReadingTime,
        DateTimeOffset? PublishedAt,
        string? HeroImageUrl,
        string? CategoryName,
        string? CategorySlug,
        IReadOnlyList<string> Tags);

    public sealed record CategorySummary(string Name, string Slug);

    // ── Global design tokens (Elementor-style "Global Colors/Fonts") ─────────
    // Font pairings are a small curated set rather than an open-ended picker — matches
    // this codebase's "don't over-engineer" convention and avoids needing a font-loading
    // UI. "elegant" reproduces the original hardcoded Playfair+Inter pairing exactly.
    private sealed record FontPairing(string HeadingFamily, string BodyFamily, string GoogleFontsHref);

    private static readonly Dictionary<string, FontPairing> FontPairingsByKey = new()
    {
        [CmsFontPairings.Elegant] = new(
            "'Playfair Display', Georgia, 'Times New Roman', serif",
            "'Inter', system-ui, -apple-system, sans-serif",
            "https://fonts.googleapis.com/css2?family=Playfair+Display:wght@700;800;900&family=Inter:wght@300;400;500;600;700&display=swap"),
        [CmsFontPairings.Modern] = new(
            "'Manrope', system-ui, -apple-system, sans-serif",
            "'Inter', system-ui, -apple-system, sans-serif",
            "https://fonts.googleapis.com/css2?family=Manrope:wght@600;700;800&family=Inter:wght@300;400;500;600;700&display=swap"),
        [CmsFontPairings.Classic] = new(
            "'Merriweather', Georgia, 'Times New Roman', serif",
            "'Source Sans 3', system-ui, -apple-system, sans-serif",
            "https://fonts.googleapis.com/css2?family=Merriweather:wght@700;900&family=Source+Sans+3:wght@300;400;500;600;700&display=swap"),
    };

    private const string DefaultAccentColorHex = "#f59e0b";

    // ── Page shell ───────────────────────────────────────────────────────────

    public static string Layout(
        string pageTitle, string metaDescription, string? ogImageUrl, string bodyHtml,
        IReadOnlyList<NavMenuItem>? navItems = null, string? accentColorHex = null, string? fontPairingKey = null)
    {
        var ogImageTag = string.IsNullOrWhiteSpace(ogImageUrl)
            ? string.Empty
            : $"""<meta property="og:image" content="{Html(ogImageUrl)}" />""";

        var accent = string.IsNullOrWhiteSpace(accentColorHex) ? DefaultAccentColorHex : accentColorHex;
        var pairing = fontPairingKey is not null && FontPairingsByKey.TryGetValue(fontPairingKey, out var p)
            ? p
            : FontPairingsByKey[CmsFontPairings.Elegant];

        // color-mix() derives the hover/low-opacity accent shades from whatever single
        // color an admin picks, so there's no hex-math to hand-roll in C# for a custom
        // accent color to look consistent everywhere --accent-hover/--accent-low are used.
        var designTokensStyle = $$"""
            <style>
              :root {
                --accent: {{accent}};
                --accent-low: color-mix(in srgb, {{accent}} 12%, transparent);
                --accent-hover: color-mix(in srgb, {{accent}} 85%, black);
                --font-serif: {{pairing.HeadingFamily}};
                --font-sans: {{pairing.BodyFamily}};
              }
            </style>
            """;

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8" />
              <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{Html(pageTitle)}</title>
              <meta name="description" content="{Html(metaDescription)}" />
              {ogImageTag}
              <link rel="preconnect" href="https://fonts.googleapis.com" />
              <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
              <link href="{pairing.GoogleFontsHref}" rel="stylesheet" />
              <link rel="stylesheet" href="/public-site.css" />
              {designTokensStyle}
            </head>
            <body>
              {Nav(navItems ?? DefaultNavItems)}
              {bodyHtml}
              {Footer()}
            </body>
            </html>
            """;
    }

    private static string Nav(IReadOnlyList<NavMenuItem> navItems)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <nav class="site-nav">
              <a href="/" class="site-logo">
                <img src="/logo-mark.svg" alt="" />
                <span class="site-logo-wordmark">
                  <span>grantwatson</span>
                  <span class="site-logo-domain">.dev</span>
                </span>
              </a>
              <div class="nav-links">
            """);
        foreach (var item in navItems)
        {
            var targetAttrs = item.OpenInNewTab ? " target=\"_blank\" rel=\"noopener noreferrer\"" : "";
            sb.Append($"""<a href="{Html(item.Href)}"{targetAttrs}>{Html(item.Label)}</a>""");
        }
        sb.Append("</div></nav>");
        return sb.ToString();
    }

    private static string Footer() => $"""
        <footer class="site-footer">
          <p>&copy; {DateTimeOffset.UtcNow.Year} Grant Watson</p>
          <a href="/admin" class="footer-admin-link">admin</a>
        </footer>
        """;

    // ── 404 ──────────────────────────────────────────────────────────────────

    public static string NotFoundBody(string message, string backHref, string backLabel) => $"""
        <main class="page-not-found">
          <h1>404</h1>
          <p>{Html(message)}</p>
          <a href="{Html(backHref)}">&larr; {Html(backLabel)}</a>
        </main>
        """;

    // ── Form submitted banner ────────────────────────────────────────────────
    // Shown inline above a Canvas page's normal content when it's reached via
    // ?submitted=1 (the contact-form submit handler's redirect target) — replaces a
    // separate /{page}/thanks route, which can't coexist with the nested-page catch-all
    // route (a fixed segment can't follow a catch-all route parameter).

    public static string SubmittedBanner() => """
        <div class="gws-submitted-banner">Thanks — your message was sent. I'll get back to you soon.</div>
        """;

    // ── Blog list ────────────────────────────────────────────────────────────

    public static string BlogListBody(
        IReadOnlyList<ArticleSummary> pageItems,
        IReadOnlyList<string> keywords,
        string? activeKeyword,
        IReadOnlyList<CategorySummary> categories,
        string? activeCategory,
        string? activeTag,
        int page,
        int pageSize,
        int totalCount,
        int totalPages)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <main class="blog-page">
              <header class="blog-page-header">
                <h1>Blog</h1>
                <div class="blog-page-accent"></div>
                <p>Thoughts on software, building products, and the web.</p>
              </header>
            """);

        if (totalCount == 0)
        {
            sb.Append("""<p style="color:var(--text-2)">No articles yet. Check back soon.</p></main>""");
            return sb.ToString();
        }

        if (categories.Count > 0)
        {
            sb.Append("""<section class="keyword-cloud" aria-label="Filter by category"><span class="keyword-cloud-label">Category:</span>""");
            foreach (var cat in categories)
            {
                var isActive = string.Equals(cat.Slug, activeCategory, StringComparison.Ordinal);
                var href = isActive
                    ? QueryString(activeKeyword, null, activeTag, 1, pageSize)
                    : QueryString(activeKeyword, cat.Slug, activeTag, 1, pageSize);
                sb.Append($"""<a class="keyword-pill{(isActive ? " active" : "")}" href="{Html(href)}">{Html(cat.Name)}</a>""");
            }
            if (!string.IsNullOrWhiteSpace(activeCategory))
            {
                sb.Append($"""<a class="keyword-clear" href="{Html(QueryString(activeKeyword, null, activeTag, 1, pageSize))}">Clear</a>""");
            }
            sb.Append("</section>");
        }

        if (keywords.Count > 0)
        {
            sb.Append("""<section class="keyword-cloud" aria-label="Filter by keyword"><span class="keyword-cloud-label">Filter:</span>""");
            foreach (var kw in keywords)
            {
                var isActive = string.Equals(kw, activeKeyword, StringComparison.Ordinal);
                var href = isActive
                    ? QueryString(null, activeCategory, activeTag, 1, pageSize)
                    : QueryString(kw, activeCategory, activeTag, 1, pageSize);
                sb.Append($"""<a class="keyword-pill{(isActive ? " active" : "")}" href="{Html(href)}">{Html(kw)}</a>""");
            }
            if (!string.IsNullOrWhiteSpace(activeKeyword))
            {
                sb.Append($"""<a class="keyword-clear" href="{Html(QueryString(null, activeCategory, activeTag, 1, pageSize))}">Clear</a>""");
            }
            sb.Append("</section>");
        }

        if (!string.IsNullOrWhiteSpace(activeTag))
        {
            sb.Append($"""
                <section class="keyword-cloud" aria-label="Filter by tag">
                  <span class="keyword-cloud-label">Tag:</span>
                  <a class="keyword-pill active" href="{Html(QueryString(activeKeyword, activeCategory, activeTag, 1, pageSize))}">{Html(activeTag)}</a>
                  <a class="keyword-clear" href="{Html(QueryString(activeKeyword, activeCategory, null, 1, pageSize))}">Clear</a>
                </section>
                """);
        }

        var isFiltered = !string.IsNullOrWhiteSpace(activeKeyword) || !string.IsNullOrWhiteSpace(activeCategory) || !string.IsNullOrWhiteSpace(activeTag);
        var resultCountLabel = !isFiltered
            ? $"{totalCount} article{(totalCount != 1 ? "s" : "")}"
            : $"{totalCount} of {totalCount} articles";

        sb.Append($"""
            <div class="blog-controls">
              <span class="blog-result-count">{Html(resultCountLabel)}</span>
              <div class="blog-pagesize">
                <span>Show:</span>
            """);
        foreach (var n in new[] { 10, 25, 50 })
        {
            var isActive = n == pageSize;
            var href = QueryString(activeKeyword, activeCategory, activeTag, page: 1, pageSize: n);
            sb.Append($"""<a class="pagesize-btn{(isActive ? " active" : "")}" href="{Html(href)}">{n}</a>""");
        }
        sb.Append("</div></div>");

        sb.Append("""<div class="blog-grid">""");
        foreach (var a in pageItems)
        {
            sb.Append(ArticleCard(a));
        }
        sb.Append("</div>");

        if (totalPages > 1)
        {
            sb.Append("""<nav class="blog-pagination" aria-label="Page navigation">""");
            var prevHref = QueryString(activeKeyword, activeCategory, activeTag, Math.Max(1, page - 1), pageSize);
            sb.Append($"""<a class="pagination-btn{(page == 1 ? " disabled" : "")}" href="{Html(prevHref)}" aria-label="Previous page">&larr; Prev</a>""");

            for (var n = 1; n <= totalPages; n++)
            {
                var href = QueryString(activeKeyword, activeCategory, activeTag, n, pageSize);
                sb.Append($"""<a class="pagination-btn{(n == page ? " active" : "")}" href="{Html(href)}">{n}</a>""");
            }

            var nextHref = QueryString(activeKeyword, activeCategory, activeTag, Math.Min(totalPages, page + 1), pageSize);
            sb.Append($"""<a class="pagination-btn{(page == totalPages ? " disabled" : "")}" href="{Html(nextHref)}" aria-label="Next page">Next &rarr;</a>""");
            sb.Append("</nav>");
        }

        sb.Append("</main>");
        return sb.ToString();
    }

    private static string QueryString(string? keyword, string? category, string? tag, int page, int pageSize)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(keyword)) parts.Add($"keyword={Uri.EscapeDataString(keyword)}");
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(tag)) parts.Add($"tag={Uri.EscapeDataString(tag)}");
        if (page > 1) parts.Add($"page={page}");
        if (pageSize != 10) parts.Add($"pageSize={pageSize}");
        return parts.Count == 0 ? "/blog" : $"/blog?{string.Join('&', parts)}";
    }

    private static string ArticleCard(ArticleSummary a)
    {
        var desc = a.MetaDescription is { Length: > 125 }
            ? a.MetaDescription[..125] + "…"
            : a.MetaDescription;
        var dateLabel = a.PublishedAt?.ToString("MMM yyyy") ?? "";

        var imageHtml = string.IsNullOrWhiteSpace(a.HeroImageUrl)
            ? """<div class="article-card-img-placeholder">No image</div>"""
            : $"""<img src="{Html(a.HeroImageUrl)}" alt="{Html(a.Title)}" class="article-card-img" loading="lazy" />""";

        var categoryPillHtml = string.IsNullOrWhiteSpace(a.CategoryName)
            ? ""
            : $"""<span class="article-tag">{Html(a.CategoryName)}</span>""";
        var tagPillsHtml = string.Concat(a.Tags.Select(t => $"""<span class="article-tag">{Html(t)}</span>"""));

        return $"""
            <a href="/blog/{Html(a.Slug)}" class="article-card">
              {imageHtml}
              <div class="article-card-body">
                <div class="article-card-title">{Html(a.Title)}</div>
                {(string.IsNullOrWhiteSpace(desc) ? "" : $"""<p class="article-card-desc">{Html(desc)}</p>""")}
                <div class="article-card-meta">
                  {(string.IsNullOrWhiteSpace(a.EstimatedReadingTime) ? "" : $"""<span>{Html(a.EstimatedReadingTime)} read</span>""")}
                  {(string.IsNullOrWhiteSpace(a.PrimaryKeyword) ? "" : $"""<span class="article-tag">{Html(a.PrimaryKeyword)}</span>""")}
                  {categoryPillHtml}
                  {tagPillsHtml}
                  <span>{Html(dateLabel)}</span>
                </div>
              </div>
            </a>
            """;
    }

    // ── Blog post ────────────────────────────────────────────────────────────

    public static string BlogPostBody(
        string title,
        string metaDescription,
        string author,
        DateTimeOffset? publishedAt,
        string estimatedReadingTime,
        string primaryKeyword,
        string? heroImageUrl,
        string heroImageAltText,
        string heroImageCaption,
        string bodyMarkdown,
        string? categoryName = null,
        string? categorySlug = null,
        IReadOnlyList<string>? tags = null)
    {
        var dateLabel = publishedAt?.ToString("MMMM d, yyyy") ?? "";
        var heroHtml = string.IsNullOrWhiteSpace(heroImageUrl)
            ? string.Empty
            : $"""
                <div class="blog-post-hero-wrap">
                  <img src="{Html(heroImageUrl)}" alt="{Html(string.IsNullOrWhiteSpace(heroImageAltText) ? title : heroImageAltText)}" />
                </div>
                {(string.IsNullOrWhiteSpace(heroImageCaption) ? "" : $"""<p class="blog-post-hero-caption">{Html(heroImageCaption)}</p>""")}
                """;

        var bodyHtml = string.IsNullOrWhiteSpace(bodyMarkdown)
            ? """<p style="color:var(--text-3);font-style:italic">No content available for this article.</p>"""
            : RenderMarkdown(bodyMarkdown);

        // Unlike the card (which is itself a single <a> wrapping everything), a post page has
        // no such constraint - category/tag pills here are real clickable filters back to /blog.
        var categoryPillHtml = string.IsNullOrWhiteSpace(categoryName)
            ? ""
            : $"""<a class="article-tag" href="/blog?category={Uri.EscapeDataString(categorySlug ?? "")}">{Html(categoryName)}</a>""";
        var tagPillsHtml = tags is null
            ? ""
            : string.Concat(tags.Select(t => $"""<a class="article-tag" href="/blog?tag={Uri.EscapeDataString(t)}">{Html(t)}</a>"""));

        return $"""
            <article>
              {heroHtml}
              <div class="blog-post-content">
                <div class="blog-post-meta-row">
                  <span>{Html(dateLabel)}</span>
                  {(string.IsNullOrWhiteSpace(estimatedReadingTime) ? "" : $"""<span>&middot; {Html(estimatedReadingTime)} read</span>""")}
                  {(string.IsNullOrWhiteSpace(primaryKeyword) ? "" : $"""<span class="article-tag">{Html(primaryKeyword)}</span>""")}
                  {categoryPillHtml}
                  {tagPillsHtml}
                </div>
                <h1 class="blog-post-title">{Html(title)}</h1>
                {(string.IsNullOrWhiteSpace(metaDescription) ? "" : $"""<p class="blog-post-lead">{Html(metaDescription)}</p>""")}
                <p class="blog-post-author">By {Html(author)}</p>
                <div class="blog-post-body">{bodyHtml}</div>
                <footer class="blog-post-footer">
                  <a href="/blog">&larr; All articles</a>
                </footer>
              </div>
            </article>
            """;
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
