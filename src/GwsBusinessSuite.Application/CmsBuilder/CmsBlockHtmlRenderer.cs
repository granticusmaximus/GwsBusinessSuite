using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Markdig;

namespace GwsBusinessSuite.Application.CmsBuilder;

/// <summary>
/// Renders a CmsPage's BlocksJson — a PageLayout-shaped Section/Column/Widget document,
/// the same schema the Studio (CmsBuilderEditor.razor) edits — to a public-facing HTML
/// fragment. Mirrors the widget vocabulary and prop-key conventions of the admin preview
/// (CmsBlockPreview.razor) so both stay in sync, but this one produces plain HTML strings
/// so it can run outside the Blazor render pipeline, from a minimal API endpoint. This is
/// the single rendering codepath shared by the Studio's own live-preview iframe, the real
/// public site, and the static export feature — see Program.cs's three call sites.
/// </summary>
// A pre-fetched, already-publicly-visible-filtered article for the "posts-grid" widget -
// the renderer stays a pure function with no DB access of its own, so callers (Program.cs's
// three Render() call sites) load this once per request and pass it through.
public sealed record PublicArticleSummary(string Slug, string Title, string MetaDescription, string? HeroImageUrl, DateTimeOffset? PublishedAt);

public static class CmsBlockHtmlRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly IReadOnlyList<PublicArticleSummary> NoArticles = [];

    // Lets callers skip fetching PublicArticleSummary data entirely for the (common) case
    // of a page with no posts-grid widget at all, rather than unconditionally querying the
    // Articles table on every public page render regardless of whether anything on the
    // page would use it.
    public static bool LayoutContainsPostsGrid(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "posts-grid")));

    // Same "skip the query when nothing on the page needs it" reasoning as LayoutContainsPostsGrid
    // above, but a page can carry more than one related-posts block pointed at different anchor
    // articles (e.g. a landing page built around two different cornerstone pieces), so the caller
    // needs the distinct set of anchors to resolve, not just a yes/no.
    public static bool LayoutContainsRelatedPosts(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "related-posts")));

    public static IReadOnlyList<string> GetRelatedPostsAnchorSlugs(PageLayout? layout)
    {
        if (layout is null) return [];
        return layout.Sections
            .SelectMany(s => s.Columns)
            .SelectMany(c => c.Widgets)
            .Where(w => w.WidgetType == "related-posts")
            .Select(w => Get(w.Props, "sourceArticleSlug"))
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Phase 4 (JS-requiring widgets) - lets Program.cs skip emitting the (small but non-zero)
    // TOC/reading-progress runtime scripts on the majority of pages that use neither, same
    // "skip work nothing on the page needs" reasoning as LayoutContainsPostsGrid above. Unlike
    // posts-grid/related-posts this gates no DB query - both scripts are pure static strings -
    // it's purely a payload-size guard.
    public static bool LayoutContainsTableOfContents(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "table-of-contents")));

    public static bool LayoutContainsReadingProgress(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "reading-progress")));

    // Workstream C, Tier 2 (stats/counter block) - same payload-size-guard reasoning as the
    // table-of-contents/reading-progress guards above.
    public static bool LayoutContainsStats(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "stats")));

    // Workstream C, Tier 2 (tabs widget) - same payload-size-guard reasoning as the guards above.
    public static bool LayoutContainsEmailSignup(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "email-signup")));

    public static bool LayoutContainsTabs(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "tabs")));

    // Workstream C, Tier 3 (pricing table monthly/yearly toggle) - same payload-size-guard
    // reasoning as the guards above.
    public static bool LayoutContainsPricingTable(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "pricing-table")));

    // Workstream C, Tier 3 (gallery/image grid lightbox) - same payload-size-guard reasoning as
    // the guards above.
    public static bool LayoutContainsGallery(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "gallery")));

    // Workstream C, Tier 3 (carousel / testimonial slider) - both widget types render into the
    // identical .gws-carousel DOM shell (see RenderCarousel/RenderTestimonialSlider) and share
    // ONE runtime script, so one guard covers both rather than two near-duplicate guards each
    // gating the same script.
    public static bool LayoutContainsCarousel(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType is "carousel" or "testimonial-slider")));

    // Workstream C, Tier 3 (portfolio/project grid) - same payload-size-guard reasoning as the
    // guards above.
    public static bool LayoutContainsPortfolioGrid(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "portfolio-grid")));

    // A short single-line preview of a widget's content, used by the structural revision diff
    // (PageRevisionService.BuildStructuralDiff) - never HTML, just text. Mirrors
    // WikiBlockHtmlRenderer.PlainTextPreview's role for wiki blocks.
    public static string PlainTextPreview(LayoutWidget widget, int maxLength = 80)
    {
        var p = widget.Props;
        var text = widget.WidgetType switch
        {
            "hero" => Get(p, "headline"),
            "heading" => Get(p, "text"),
            "paragraph" => Get(p, "text"),
            "richtext" => Get(p, "content"),
            "button" => Get(p, "label"),
            "image" => Get(p, "alt", Get(p, "src", "[image]")),
            "card" => Get(p, "title"),
            "testimonial" => Get(p, "quote"),
            "spacer" => "[spacer]",
            "divider" => "---",
            "html" => "[custom HTML]",
            "form" => "[form]",
            "posts-grid" => "[posts grid]",
            "accordion" => "[accordion]",
            "author-box" => Get(p, "name"),
            "callout" => Get(p, "title", Get(p, "body", "[callout]")),
            "related-posts" => HasValue(p, "sourceArticleSlug") ? $"[related posts: {Get(p, "sourceArticleSlug")}]" : "[related posts]",
            "table-of-contents" => "[table of contents]",
            "reading-progress" => "[reading progress bar]",
            "booking" => HasValue(p, "bookingTypeSlug") ? $"[booking: {Get(p, "bookingTypeSlug")}]" : "[booking]",
            "stats" => "[stats]",
            "cta-banner" => Get(p, "headline", "[CTA banner]"),
            "email-signup" => Get(p, "heading", "[email signup]"),
            "map" => Get(p, "address", "[map]"),
            "site-logo" => "[site logo]",
            "nav-menu" => $"[{Get(p, "menu", "header")} menu]",
            "copyright" => $"© {Get(p, "holder")}",
            "team-grid" => "[team grid]",
            "logo-cloud" => "[logo cloud]",
            "process-steps" => "[process steps]",
            "tabs" => "[tabs]",
            "pricing-table" => "[pricing table]",
            "gallery" => "[gallery]",
            "carousel" => "[carousel]",
            "testimonial-slider" => "[testimonial slider]",
            "portfolio-grid" => "[portfolio grid]",
            "case-study" => Get(p, "title", "[case study]"),
            _ => string.Empty
        };
        text = text.Replace('\n', ' ').Trim();
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }

    // isLoggedIn only matters for VisibilityModes.LoggedInOnly widgets and defaults to false
    // (the safe default for the two call sites - static export and the fully-anonymous public
    // canvas route - that have no concept of a logged-in visitor at all). The one route that
    // does (admin.gwsapp.net's /cms/{siteSlug}/{**pageSlug} preview route) passes its own
    // already-computed IsAuthenticated check through explicitly.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>> NoRelatedPosts =
        new Dictionary<string, IReadOnlyList<RelatedArticleView>>();

    public static string Render(string blocksJson, string siteSlug = "", string pageSlug = "", bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null, bool isLoggedIn = false, DesignTokenSet? tokens = null, IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>>? relatedPostsByAnchorSlug = null, string? turnstileSiteKey = null)
        => Render(CmsBuilderJson.ParseLayout(blocksJson), siteSlug, pageSlug, editMode, articles, isLoggedIn, tokens, relatedPostsByAnchorSlug, turnstileSiteKey);

    public static string Render(PageLayout? layout, string siteSlug = "", string pageSlug = "", bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null, bool isLoggedIn = false, DesignTokenSet? tokens = null, IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>>? relatedPostsByAnchorSlug = null, string? turnstileSiteKey = null)
    {
        if (layout is null || layout.Sections.Count == 0)
        {
            return editMode
                ? """<div class="gws-canvas-empty" data-gws-empty-canvas="1">Drop widgets here to start building this page.</div>"""
                : string.Empty;
        }

        var effectiveArticles = articles ?? NoArticles;
        var effectiveRelatedPosts = relatedPostsByAnchorSlug ?? NoRelatedPosts;
        var html = new StringBuilder();
        foreach (var section in layout.Sections)
        {
            html.Append(RenderSection(section, siteSlug, pageSlug, editMode, effectiveArticles, isLoggedIn, tokens, effectiveRelatedPosts, turnstileSiteKey));
        }

        if (!editMode && !string.IsNullOrWhiteSpace(turnstileSiteKey)
            && layout.Sections.Any(s => s.Columns.Any(c => c.Widgets.Any(w => w.WidgetType == "form"))))
        {
            html.Append("""<script src="/js/contact-form.js"></script><script src="https://challenges.cloudflare.com/turnstile/v0/api.js?onload=gwsContactTurnstileReady&amp;render=explicit" async defer></script>""");
        }
        // External file (not an inline runtime script) so it runs under the site's CSP. It loads
        // Turnstile itself, only when the server has it configured.
        if (!editMode && LayoutContainsEmailSignup(layout))
        {
            html.Append("""<script src="/js/email-signup.js" defer></script>""");
        }
        return html.ToString();
    }

    // Part 6.3 - a widget with no visibility rule (Mode == Always, the default) always
    // renders. In edit mode the caller (RenderSection) never calls this - Studio always shows
    // every widget regardless of the rule, so an author can still see/select/edit it; a small
    // badge (see VisibilityBadgeText) marks it as conditional instead.
    public static bool ShouldRenderWidget(VisibilityRule visibility, string pageSlug, bool isLoggedIn) => visibility.Mode switch
    {
        VisibilityModes.LoggedInOnly => isLoggedIn,
        VisibilityModes.HomepageOnly => string.Equals(pageSlug.Trim('/'), "home", StringComparison.OrdinalIgnoreCase),
        VisibilityModes.UrlPattern => MatchesUrlPattern(pageSlug, visibility.UrlPattern),
        _ => true
    };

    private static bool MatchesUrlPattern(string pageSlug, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        var normalizedSlug = pageSlug.Trim('/');
        var regexPattern = "^" + Regex.Escape(pattern.Trim('/')).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(normalizedSlug, regexPattern, RegexOptions.IgnoreCase);
    }

    private static string VisibilityBadgeText(VisibilityRule visibility) => visibility.Mode switch
    {
        VisibilityModes.LoggedInOnly => "Logged-in only",
        VisibilityModes.HomepageOnly => "Homepage only",
        VisibilityModes.UrlPattern when !string.IsNullOrWhiteSpace(visibility.UrlPattern) => $"URL: {visibility.UrlPattern}",
        VisibilityModes.UrlPattern => "URL pattern",
        _ => string.Empty
    };

    // Phase 2 (client-safe structural locking) - informational only, shown to every Studio user
    // regardless of role (same as VisibilityBadgeText). Only for an EXPLICIT, non-inherited
    // value set directly on this widget/section - a value inherited from the page's own default
    // isn't shown here, since CmsBlockHtmlRenderer is a stateless renderer with no access to the
    // CmsPage that owns this layout (see Render's own doc comment) and threading that through
    // just for a badge isn't worth the plumbing this phase already avoided for the same reason.
    private static string EditPermissionBadgeText(string editPermission) => editPermission switch
    {
        CmsEditPermissions.Locked => "Locked",
        CmsEditPermissions.ContentOnly => "Content only",
        _ => string.Empty
    };

    // Emitted only when editMode is true (see Program.cs's /cms/{siteSlug}/{**pageSlug}
    // gating - never reaches a real visitor). Lets Canvas Studio's live-preview iframe
    // report clicks back to the parent page via postMessage instead of navigating away,
    // and highlights the currently-selected element. See cms-builder-bridge.js for the
    // parent-side half of this bridge.
    // The <style> block stays inline (style-src allows 'unsafe-inline'); the behaviour is served
    // from /js/cms-edit-mode.js because script-src does not allow inline scripts - see that file's
    // header. Keeping it inline meant the canvas was inert in every deployed environment.
    public static string BuildEditModeScript() => """
        <style>
          .gws-editable { position: relative; }
          .gws-editable:hover { outline: 1px dashed rgba(37, 99, 235, 0.45); outline-offset: -1px; cursor: pointer; }
          .gws-editor-selected { outline: 2px solid #2563eb !important; outline-offset: -2px; }
          [data-gws-section-id]:hover { outline: 1px dashed rgba(148, 163, 184, 0.5); outline-offset: -1px; }
          /* Sections previously had only a hover outline, so clicking one produced no lasting
             visual change and the click read as dead. This is the section equivalent of
             .gws-editor-selected. */
          .gws-section-selected { outline: 2px solid #2563eb !important; outline-offset: -2px; }
          [data-gws-section-id] { position: relative; }
          .gws-section-handle {
            position: absolute; top: 0; left: 0; z-index: 2147482000;
            appearance: none; border: 0; cursor: pointer;
            background: rgba(100, 116, 139, 0.85); color: #fff;
            font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
            font-size: 10px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase;
            padding: 3px 8px; border-radius: 0 0 6px 0; opacity: 0; transition: opacity .12s ease;
          }
          [data-gws-section-id]:hover .gws-section-handle,
          .gws-section-selected .gws-section-handle { opacity: 1; }
          .gws-section-selected .gws-section-handle { background: #2563eb; }
          /* Anchored to the section's top-RIGHT: the section's name chip sits at top-left, and
             at top-left the two overlapped each other. */
          .gws-section-toolbar {
            position: absolute; z-index: 2147483000; display: flex; gap: 2px;
            transform: translateY(-100%);
            background: #1e293b; border-radius: 8px 8px 0 0; padding: 4px;
            box-shadow: 0 6px 18px rgba(15, 23, 42, 0.28);
            font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
          }
          .gws-section-toolbar button {
            appearance: none; border: 0; background: transparent; color: #e2e8f0;
            font-size: 12px; line-height: 1; padding: 6px 9px; border-radius: 5px; cursor: pointer;
          }
          .gws-section-toolbar button:hover { background: rgba(148, 163, 184, 0.25); color: #fff; }
          .gws-section-toolbar button.is-primary { background: #2563eb; color: #fff; font-weight: 600; }
          .gws-section-toolbar button.is-primary:hover { background: #1d4ed8; }
          .gws-section-toolbar button.is-danger:hover { background: #b91c1c; color: #fff; }
          /* Same anchoring trick as .gws-section-toolbar, retargeted to the widget itself
             (.gws-editable is already position:relative). Floats above the widget's own top
             edge via translateY(-100%), so it never collides with .gws-drag-handle (top-left,
             inside the widget) or .gws-visibility-hint (top-right, inside the widget). */
          .gws-widget-toolbar {
            position: absolute; z-index: 2147483000; display: flex; gap: 2px;
            transform: translateY(-100%);
            background: #1e293b; border-radius: 8px 8px 0 0; padding: 4px;
            box-shadow: 0 6px 18px rgba(15, 23, 42, 0.28);
            font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
          }
          .gws-widget-toolbar button {
            appearance: none; border: 0; background: transparent; color: #e2e8f0;
            font-size: 12px; line-height: 1; padding: 6px 9px; border-radius: 5px; cursor: pointer;
          }
          .gws-widget-toolbar button:hover { background: rgba(148, 163, 184, 0.25); color: #fff; }
          .gws-widget-toolbar button.is-danger:hover { background: #b91c1c; color: #fff; }
          /* Selection formatting bar. Deliberately carries only bold / italic / link: those are
             exactly what the HTML->Markdown serializer can carry back, so the toolbar doubles as
             an honest boundary of what inline editing supports. */
          .gws-format-bar {
            position: absolute; z-index: 2147483600; display: none; gap: 2px;
            background: #1e293b; border-radius: 7px; padding: 4px;
            box-shadow: 0 8px 20px rgba(15, 23, 42, 0.35);
            font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
          }
          .gws-format-bar.is-open { display: flex; }
          .gws-format-bar button {
            appearance: none; border: 0; background: transparent; color: #e2e8f0;
            min-width: 28px; height: 26px; padding: 0 8px; border-radius: 5px; cursor: pointer;
            font-size: 13px; line-height: 1;
          }
          .gws-format-bar button:hover { background: rgba(148, 163, 184, 0.3); color: #fff; }
          .gws-format-bar button.is-active { background: #2563eb; color: #fff; }
          [data-gws-inline-rich]:focus { outline: 2px solid #16a34a; outline-offset: 3px; cursor: text; }
          [data-gws-inline-prop]:focus { outline: 2px solid #16a34a !important; outline-offset: 2px; cursor: text; }
          .gws-column { position: relative; min-height: 24px; }
          .gws-column.is-drop-target { outline: 1px dashed rgba(37, 99, 235, 0.45); outline-offset: 6px; border-radius: 14px; }
          .gws-column-empty {
            min-height: 72px; border: 1px dashed rgba(148, 163, 184, 0.5); border-radius: 14px;
            display: flex; align-items: center; justify-content: center; text-align: center;
            color: #64748b; font-size: 0.9rem; background: rgba(248, 250, 252, 0.9);
          }
          .gws-canvas-empty {
            min-height: 240px; margin: 2rem auto; padding: 1.5rem;
            border: 2px dashed rgba(148, 163, 184, 0.65); border-radius: 20px;
            display: flex; align-items: center; justify-content: center; text-align: center;
            color: #475569; background: linear-gradient(180deg, rgba(248, 250, 252, 0.95), rgba(241, 245, 249, 0.95));
          }
          .gws-canvas-empty.is-drop-target { border-color: #2563eb; background: rgba(219, 234, 254, 0.55); }
          .gws-drag-handle {
            position: absolute; top: 4px; left: 4px; z-index: 40;
            width: 28px; height: 28px; border-radius: 6px;
            background: #2563eb; color: #fff;
            display: flex; align-items: center; justify-content: center;
            font-size: 13px; line-height: 1; cursor: grab;
            opacity: 0; transition: opacity 0.1s ease;
          }
          .gws-editable:hover .gws-drag-handle, .gws-editor-selected .gws-drag-handle { opacity: 1; }
          .gws-drag-handle:active { cursor: grabbing; }
          .gws-visibility-hint {
            position: absolute; top: 4px; right: 4px; z-index: 39;
            background: #f59e0b; color: #1c1917; font-size: 11px; line-height: 1.4;
            padding: 1px 7px; border-radius: 999px; opacity: 0; transition: opacity 0.1s ease;
            pointer-events: none; white-space: nowrap;
          }
          .gws-editable:hover .gws-visibility-hint { opacity: 1; }
          .gws-section-freeform-canvas { position: relative; }
          .gws-freeform-item { outline: 1px dashed rgba(148, 163, 184, 0.4); outline-offset: -1px; cursor: move; }
          .gws-freeform-item:hover { outline-color: rgba(37, 99, 235, 0.45); }
          .gws-freeform-resize {
            position: absolute; right: -5px; bottom: -5px; z-index: 41;
            width: 14px; height: 14px; border-radius: 3px;
            background: #2563eb; border: 2px solid #fff;
            cursor: nwse-resize; opacity: 0; transition: opacity 0.1s ease;
          }
          .gws-editable:hover .gws-freeform-resize, .gws-editor-selected .gws-freeform-resize { opacity: 1; }
        </style>
        <script src="/js/cms-edit-mode.js" defer></script>
        """;

    // Phase 2 (Hide on mobile/tablet) - the space-joined class fragment for whichever of the
    // two width-scoped hide classes are set; empty when neither is, so a section/widget with
    // both flags false composes exactly as it did before this feature existed.
    private static string HiddenClasses(bool hiddenOnMobile, bool hiddenOnTablet)
    {
        var classes = new List<string>(2);
        if (hiddenOnMobile) classes.Add("gws-hide-mobile");
        if (hiddenOnTablet) classes.Add("gws-hide-tablet");
        return string.Join(' ', classes);
    }

    // Page Editor Phase 4 (Section-level Color Scheme shortcut) - resolves to "" when the
    // section has no BackgroundColorToken (or it doesn't match a real token) and no TextColor,
    // so an untouched section renders with zero extra markup, same "no-op by default" contract
    // as WidgetStyle.ToInlineStyle. Reuses WidgetStyle.ResolveColor rather than duplicating its
    // token-lookup-with-raw-fallback rule. Public so CmsBuilderEditor.razor's live-preview push
    // (PushSectionAppearanceToCanvasAsync) can compute the exact same style string instead of
    // re-deriving it.
    public static string SectionInlineStyle(LayoutSection section, DesignTokenSet? tokens)
    {
        var parts = new List<string>();
        var backgroundColor = WidgetStyle.ResolveColor(section.BackgroundColorToken, "", tokens);
        if (!string.IsNullOrWhiteSpace(backgroundColor)) parts.Add($"background-color:{backgroundColor}");
        if (!string.IsNullOrWhiteSpace(section.TextColor)) parts.Add($"color:{section.TextColor}");
        return string.Join(';', parts);
    }

    private static string RenderSection(LayoutSection section, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles, bool isLoggedIn, DesignTokenSet? tokens, IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>> relatedPostsByAnchorSlug, string? turnstileSiteKey)
    {
        var sectionClass = $"gws-section {BgClass(section.Background)} {PadClass(section.Padding)} {HiddenClasses(section.HiddenOnMobile, section.HiddenOnTablet)}".TrimEnd();
        var sectionStyle = SectionInlineStyle(section, tokens);
        var sectionAttrs = (editMode ? $" data-gws-section-id=\"{Html(section.Id)}\"" : "")
            + (sectionStyle.Length == 0 ? "" : $" style=\"{Html(sectionStyle)}\"");

        if (section.LayoutMode == CmsSectionLayoutModes.Freeform)
        {
            return RenderFreeformSection(section, sectionClass, sectionAttrs, siteSlug, pageSlug, editMode, articles, isLoggedIn, tokens, relatedPostsByAnchorSlug, turnstileSiteKey);
        }

        var columnsClass = ColsClass(section.ColumnLayout);
        var sb = new StringBuilder();
        sb.Append($"""<section class="{Html(sectionClass)}"{sectionAttrs}>{SectionHandle(section, editMode)}<div class="{Html(columnsClass)}">""");

        foreach (var column in section.Columns)
        {
            var columnAttrs = editMode
                ? $" class=\"gws-column\" data-gws-column-id=\"{Html(column.Id)}\""
                : " class=\"gws-column\"";
            sb.Append($"""<div{columnAttrs}>""");
            if (editMode && column.Widgets.Count == 0)
            {
                sb.Append("""<div class="gws-column-empty">Drop widgets here</div>""");
            }
            foreach (var widget in column.Widgets)
            {
                // Outside edit mode, a widget whose visibility rule doesn't match this
                // request is skipped entirely - no DOM at all, not just hidden via CSS, so a
                // "logged-in only" widget's content never reaches an anonymous response body
                // (matters for the static export in particular, which has no auth boundary of
                // its own to fall back on). In edit mode every widget always renders so an
                // author can still find and edit it; VisibilityBadgeText marks it instead.
                if (!editMode && !ShouldRenderWidget(widget.Visibility, pageSlug, isLoggedIn))
                {
                    continue;
                }

                // Interaction wrapping is skipped in edit mode - its CSS starts a pageLoad/
                // scrollIntoView widget at opacity:0 until the public-only runtime script
                // (BuildInteractionRuntimeScript, never injected into the Canvas Studio
                // preview iframe) reveals it, which would otherwise make the widget disappear
                // in the editor with nothing to ever bring it back.
                var inner = WrapWidget(RenderWidget(widget, siteSlug, pageSlug, editMode, articles, relatedPostsByAnchorSlug, tokens, turnstileSiteKey), widget, tokens);
                if (!editMode) inner = WrapWithInteraction(inner, widget.Interaction);
                // Both badges share one absolutely-positioned corner slot (see .gws-visibility-
                // hint), so a widget with both a visibility rule and a lock setting gets ONE
                // combined badge rather than two stacked/overlapping divs.
                var widgetBadgeText = editMode
                    ? string.Join(" | ", new[] { VisibilityBadgeText(widget.Visibility), EditPermissionBadgeText(widget.EditPermission) }
                        .Where(badge => badge.Length > 0))
                    : string.Empty;
                var hiddenHint = widgetBadgeText.Length > 0
                    ? $"""<div class="gws-visibility-hint">{Html(widgetBadgeText)}</div>"""
                    : string.Empty;
                // Wrapped OUTSIDE WrapWidget so a widget's own background/padding
                // overrides can never clip the selection outline, and closest('[data-gws-
                // widget-id]') in the edit-mode script always resolves reliably regardless
                // of per-widget style config.
                sb.Append(editMode
                    ? $"""<div class="gws-editable" data-gws-widget-id="{Html(widget.Id)}" data-gws-widget-type="{Html(widget.WidgetType)}">{hiddenHint}<div class="gws-drag-handle" title="Drag to move block" data-gws-drag-handle-for="{Html(widget.Id)}">&#10247;</div>{inner}</div>"""
                    : inner);
            }
            sb.Append("</div>");
        }

        sb.Append("</div></section>\n");
        return sb.ToString();
    }

    // Phase 4 (Freeform Canvas Layout) - an alternative to the column-grid RenderSection path
    // above, for a section whose LayoutMode is Freeform. Every widget lives in Columns[0]
    // (ColumnLayout/multiple columns are a Flow-only concept) and is absolutely positioned
    // inside a fixed-height canvas via its own LayoutWidget.Freeform box instead of flowing
    // through a grid. See cms-public.css/public-site.css's .gws-section-freeform-canvas /
    // .gws-freeform-item rules for the actual positioning + the small-viewport stack fallback.
    private static string RenderFreeformSection(LayoutSection section, string sectionClass, string sectionAttrs, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles, bool isLoggedIn, DesignTokenSet? tokens, IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>> relatedPostsByAnchorSlug, string? turnstileSiteKey)
    {
        var widgets = section.Columns.Count > 0 ? section.Columns[0].Widgets : [];
        var canvasAttrs = editMode
            ? $" data-gws-column-id=\"{Html(section.Columns.FirstOrDefault()?.Id ?? "")}\" data-gws-freeform=\"1\""
            : "";

        var sb = new StringBuilder();
        sb.Append($"""<section class="{Html(sectionClass)}"{sectionAttrs}><div class="gws-section-freeform-canvas" style="height:{section.FreeformHeightPx}px"{canvasAttrs}>""");

        if (editMode && widgets.Count == 0)
        {
            sb.Append("""<div class="gws-column-empty">Drop widgets here</div>""");
        }

        for (var i = 0; i < widgets.Count; i++)
        {
            var widget = widgets[i];
            if (!editMode && !ShouldRenderWidget(widget.Visibility, pageSlug, isLoggedIn))
            {
                continue;
            }

            var position = widget.Freeform ?? FreeformPosition.DefaultFor(i);
            var inner = WrapWidget(RenderWidget(widget, siteSlug, pageSlug, editMode, articles, relatedPostsByAnchorSlug, tokens, turnstileSiteKey), widget, tokens);
            if (!editMode) inner = WrapWithInteraction(inner, widget.Interaction);
            var widgetBadgeText = editMode
                ? string.Join(" | ", new[] { VisibilityBadgeText(widget.Visibility), EditPermissionBadgeText(widget.EditPermission) }
                    .Where(badge => badge.Length > 0))
                : string.Empty;
            var hiddenHint = widgetBadgeText.Length > 0
                ? $"""<div class="gws-visibility-hint">{Html(widgetBadgeText)}</div>"""
                : string.Empty;
            var positionStyle = Html(position.ToInlineStyle());
            var resizeHandle = editMode ? $"""<div class="gws-freeform-resize" data-gws-freeform-resize-for="{Html(widget.Id)}"></div>""" : string.Empty;

            sb.Append(editMode
                ? $"""<div class="gws-editable gws-freeform-item" data-gws-widget-id="{Html(widget.Id)}" data-gws-widget-type="{Html(widget.WidgetType)}" style="{positionStyle}">{hiddenHint}{resizeHandle}{inner}</div>"""
                : $"""<div class="gws-freeform-item" style="{positionStyle}">{inner}</div>""");
        }

        sb.Append("</div></section>\n");
        return sb.ToString();
    }

    // Wraps a widget's rendered HTML in a container when it has any per-widget style override
    // (Phase 6) and/or a Hide on mobile/tablet flag (Phase 2) set — otherwise returns the inner
    // HTML untouched, so a widget with neither renders byte-for-byte as it did before either
    // feature existed.
    private static string WrapWidget(string innerHtml, LayoutWidget widget, DesignTokenSet? tokens = null)
    {
        var inlineStyle = widget.Style.ToInlineStyle(tokens);
        var hiddenClasses = HiddenClasses(widget.HiddenOnMobile, widget.HiddenOnTablet);
        if (inlineStyle.Length == 0 && hiddenClasses.Length == 0)
        {
            return innerHtml;
        }

        var classAttr = $"gws-widget-style {hiddenClasses}".TrimEnd();
        var styleAttr = inlineStyle.Length == 0 ? "" : $" style=\"{Html(inlineStyle)}\"";
        return $"""<div class="{Html(classAttr)}"{styleAttr}>{innerHtml}</div>""";
    }

    // Phase 5 (Native No-Code Interactions & Animation Engine) — wraps a widget's rendered
    // HTML in a data-gws-interaction container the shared runtime script
    // (BuildInteractionRuntimeScript) reads at load time. Null Interaction (the default)
    // returns the inner HTML untouched, same "opt-in wrapper" contract as WrapWidget above.
    // Trigger/Action are re-validated against the known-good sets here rather than trusted
    // as-is — BlocksJson is just a text column, so a hand-crafted save request could otherwise
    // smuggle an arbitrary string into this attribute; an unrecognized value is treated as "no
    // interaction" rather than rendered.
    private static string WrapWithInteraction(string innerHtml, WidgetInteraction? interaction)
    {
        if (interaction is null
            || !WidgetInteractionTriggers.All.Contains(interaction.Trigger)
            || !WidgetInteractionActions.All.Contains(interaction.Action))
        {
            return innerHtml;
        }

        var durationMs = Math.Clamp(interaction.DurationMs, 0, 10_000);
        var delayMs = Math.Clamp(interaction.DelayMs, 0, 10_000);
        var payload = $$"""{"trigger":"{{interaction.Trigger}}","action":"{{interaction.Action}}","durationMs":{{durationMs}},"delayMs":{{delayMs}},"once":{{(interaction.Once ? "true" : "false")}}}""";
        return $"""<div class="gws-interaction" data-gws-interaction="{Html(payload)}">{innerHtml}</div>""";
    }

    // The site's CSP has no 'unsafe-inline' in script-src, so an inline <script> never runs on a
    // served page. Every widget runtime below therefore has ONE source (its Build*RuntimeScript
    // string): served pages load it from /js/cms-runtime/{name}.js (Program.cs strips the
    // <script> wrapper), while the static ZIP export - which has no CSP - still inlines it.
    public static readonly IReadOnlyDictionary<string, Func<string>> RuntimeScripts = new Dictionary<string, Func<string>>(StringComparer.Ordinal)
    {
        ["interactions"] = BuildInteractionRuntimeScript,
        ["table-of-contents"] = BuildTableOfContentsRuntimeScript,
        ["reading-progress"] = BuildReadingProgressRuntimeScript,
        ["stats"] = BuildStatsCounterRuntimeScript,
        ["tabs"] = BuildTabsRuntimeScript,
        ["pricing-table"] = BuildPricingTableRuntimeScript,
        ["gallery"] = BuildGalleryRuntimeScript,
        ["carousel"] = BuildCarouselRuntimeScript,
        ["portfolio"] = BuildPortfolioRuntimeScript,
        ["home-blog-grid"] = () => GrantWatsonHomepageTemplate.BlogGridRuntimeScript
    };

    private static readonly Lazy<string> RuntimeScriptsVersion = new(() =>
    {
        var all = string.Concat(RuntimeScripts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value()));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all)))[..12].ToLowerInvariant();
    });

    // The JavaScript body of a runtime (no <script> wrapper), or null for an unknown name.
    public static string? RuntimeScriptSource(string name)
    {
        if (!RuntimeScripts.TryGetValue(name, out var build)) return null;
        var script = build().Trim();
        if (script.StartsWith("<script>", StringComparison.Ordinal)) script = script["<script>".Length..];
        if (script.EndsWith("</script>", StringComparison.Ordinal)) script = script[..^"</script>".Length];
        return script.Trim() + "\n";
    }

    // External <script src> tags for every runtime this page actually needs, for served pages.
    public static string BuildRuntimeScriptTags(PageLayout? layout, string renderedBodyHtml)
    {
        var needed = new List<string>();
        if (renderedBodyHtml.Contains("data-gws-interaction", StringComparison.Ordinal)) needed.Add("interactions");
        if (LayoutContainsTableOfContents(layout)) needed.Add("table-of-contents");
        if (LayoutContainsReadingProgress(layout)) needed.Add("reading-progress");
        if (LayoutContainsStats(layout)) needed.Add("stats");
        if (LayoutContainsTabs(layout)) needed.Add("tabs");
        if (LayoutContainsPricingTable(layout)) needed.Add("pricing-table");
        if (LayoutContainsGallery(layout)) needed.Add("gallery");
        if (LayoutContainsCarousel(layout)) needed.Add("carousel");
        if (LayoutContainsPortfolioGrid(layout)) needed.Add("portfolio");
        if (renderedBodyHtml.Contains("data-home-blog-grid", StringComparison.Ordinal)) needed.Add("home-blog-grid");
        var version = RuntimeScriptsVersion.Value;
        return string.Concat(needed.Select(name => $"""<script src="/js/cms-runtime/{name}.js?v={version}" defer></script>"""));
    }

    // Served as /js/cms-runtime/interactions.js (see RuntimeScripts) and inlined only into the
    // static-export zip, which stays fully self-contained. A no-op when the page has no
    // data-gws-interaction elements at all.
    public static string BuildInteractionRuntimeScript() => """
        <script>
        (function () {
          var elements = document.querySelectorAll('[data-gws-interaction]');
          if (!elements.length) return;
          var prefersReducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

          elements.forEach(function (el) {
            var config;
            try { config = JSON.parse(el.getAttribute('data-gws-interaction')); } catch (e) { return; }
            el.style.setProperty('--gws-duration', (config.durationMs || 0) + 'ms');
            el.style.setProperty('--gws-delay', (config.delayMs || 0) + 'ms');
            el.setAttribute('data-gws-trigger', config.trigger);
            el.setAttribute('data-gws-action', config.action);
            if (prefersReducedMotion) { el.classList.add('gws-interaction-revealed'); return; }

            if (config.trigger === 'pageLoad') {
              requestAnimationFrame(function () { el.classList.add('gws-interaction-revealed'); });
            } else if (config.trigger === 'scrollIntoView') {
              if (!('IntersectionObserver' in window)) { el.classList.add('gws-interaction-revealed'); return; }
              var observer = new IntersectionObserver(function (entries) {
                entries.forEach(function (entry) {
                  if (entry.isIntersecting) {
                    el.classList.add('gws-interaction-revealed');
                    if (config.once !== false) observer.unobserve(el);
                  } else if (config.once === false) {
                    el.classList.remove('gws-interaction-revealed');
                  }
                });
              }, { threshold: 0.15 });
              observer.observe(el);
            } else if (config.trigger === 'click' || config.trigger === 'hover') {
              var eventName = config.trigger === 'click' ? 'click' : 'mouseenter';
              el.addEventListener(eventName, function () {
                el.classList.remove('gws-interaction-played');
                void el.offsetWidth;
                el.classList.add('gws-interaction-played');
              });
            }
          });
        })();
        </script>
        """;

    // Phase 4 (JS-requiring widgets) - matching BuildInteractionRuntimeScript's pattern: inline
    // <script>, injected once per public page response, no-ops immediately when the page has no
    // [data-gws-toc] element at all. Decision 4 in the master plan: a client-side DOM scan
    // rather than server-side sibling-widget awareness, since the renderer has no visibility
    // into what other widgets on the page will actually render as headings (a "heading" widget,
    // a richtext block's own h2/h3s, an author-box, etc. all produce real headings it can't see
    // ahead of time).
    public static string BuildTableOfContentsRuntimeScript() => """
        <script>
        (function () {
          var tocs = document.querySelectorAll('[data-gws-toc]');
          if (!tocs.length) return;
          var levelOrder = ['h2', 'h3', 'h4'];
          var usedIds = {};

          function slugify(text) {
            var base = text.toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || 'section';
            var slug = base, n = 2;
            while (usedIds[slug]) { slug = base + '-' + n; n++; }
            usedIds[slug] = true;
            return slug;
          }

          tocs.forEach(function (nav) {
            var config;
            try { config = JSON.parse(nav.getAttribute('data-gws-toc')); } catch (e) { config = {}; }
            var minIdx = Math.max(0, levelOrder.indexOf(config.minLevel || 'h2'));
            var maxIdx = Math.max(minIdx, levelOrder.indexOf(config.maxLevel || 'h3'));
            var selector = levelOrder.slice(minIdx, maxIdx + 1).join(',');
            var headings = selector ? Array.prototype.slice.call(document.querySelectorAll(selector)) : [];
            headings = headings.filter(function (h) { return !h.closest('[data-gws-toc]'); });

            var list = nav.querySelector('.gws-toc-list');
            if (!list || !headings.length) { nav.hidden = true; return; }

            var links = [];
            headings.forEach(function (heading, index) {
              if (!heading.id) heading.id = slugify(heading.textContent || ('section-' + index));
              var li = document.createElement('li');
              li.className = 'gws-toc-item gws-toc-level-' + heading.tagName.toLowerCase();
              var a = document.createElement('a');
              a.href = '#' + heading.id;
              a.textContent = (config.showNumbers ? (index + 1) + '. ' : '') + (heading.textContent || '');
              li.appendChild(a);
              list.appendChild(li);
              links.push({ heading: heading, link: a });
            });

            if ('IntersectionObserver' in window) {
              var observer = new IntersectionObserver(function (entries) {
                entries.forEach(function (entry) {
                  var match = links.find(function (l) { return l.heading === entry.target; });
                  if (!match) return;
                  if (entry.isIntersecting) {
                    links.forEach(function (l) { l.link.classList.remove('is-active'); });
                    match.link.classList.add('is-active');
                  }
                });
              }, { rootMargin: '0px 0px -70% 0px' });
              headings.forEach(function (h) { observer.observe(h); });
            }
          });
        })();
        </script>
        """;

    // Phase 4 (JS-requiring widgets) - same no-op-when-absent pattern as the interaction/TOC
    // scripts above. One rAF-throttled scroll handler drives every reading-progress bar on the
    // page (there is realistically ever only one, but nothing stops an admin adding more).
    public static string BuildReadingProgressRuntimeScript() => """
        <script>
        (function () {
          var bars = document.querySelectorAll('[data-gws-reading-progress]');
          if (!bars.length) return;
          var ticking = false;

          function update() {
            ticking = false;
            var scrollable = document.documentElement.scrollHeight - window.innerHeight;
            var progress = scrollable > 0 ? Math.min(1, Math.max(0, window.scrollY / scrollable)) : 0;
            bars.forEach(function (bar) {
              var fill = bar.querySelector('.gws-reading-progress-bar');
              if (fill) fill.style.width = (progress * 100) + '%';
            });
          }

          function onScroll() {
            if (ticking) return;
            ticking = true;
            requestAnimationFrame(update);
          }

          window.addEventListener('scroll', onScroll, { passive: true });
          window.addEventListener('resize', onScroll);
          update();
        })();
        </script>
        """;

    // Workstream C, Tier 2 (stats/counter block) - same no-op-when-absent pattern as the other
    // runtime scripts. Each counter animates independently once its own element scrolls into
    // view (IntersectionObserver, matching BuildInteractionRuntimeScript's scrollIntoView
    // trigger), rather than all counters starting together when the page loads regardless of
    // scroll position. Decimal places are inferred from the target string itself (e.g. "99.9"
    // keeps one decimal place throughout the animation) rather than always formatting as a
    // whole number.
    public static string BuildStatsCounterRuntimeScript() => """
        <script>
        (function () {
          var counters = document.querySelectorAll('[data-gws-counter-target]');
          if (!counters.length) return;
          var prefersReducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

          function animate(el, target, decimals) {
            if (prefersReducedMotion) { el.textContent = target.toFixed(decimals); return; }
            var duration = 1500;
            var start = null;
            function step(ts) {
              if (start === null) start = ts;
              var progress = Math.min((ts - start) / duration, 1);
              var eased = 1 - Math.pow(1 - progress, 3);
              el.textContent = (target * eased).toFixed(decimals);
              if (progress < 1) requestAnimationFrame(step);
              else el.textContent = target.toFixed(decimals);
            }
            requestAnimationFrame(step);
          }

          if (!('IntersectionObserver' in window)) {
            counters.forEach(function (el) {
              var raw = el.getAttribute('data-gws-counter-target');
              var decimals = raw.includes('.') ? raw.split('.')[1].length : 0;
              animate(el, parseFloat(raw), decimals);
            });
            return;
          }

          var observer = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
              if (!entry.isIntersecting) return;
              var el = entry.target;
              var raw = el.getAttribute('data-gws-counter-target');
              var decimals = raw.includes('.') ? raw.split('.')[1].length : 0;
              animate(el, parseFloat(raw), decimals);
              observer.unobserve(el);
            });
          }, { threshold: 0.4 });
          counters.forEach(function (el) { observer.observe(el); });
        })();
        </script>
        """;

    // Workstream C, Tier 2 (tabs widget) - same no-op-when-absent pattern as the other runtime
    // scripts. Each .gws-tabs container is wired independently, so multiple tab widgets on one
    // page never interfere with each other.
    public static string BuildTabsRuntimeScript() => """
        <script>
        (function () {
          var containers = document.querySelectorAll('[data-gws-tabs]');
          if (!containers.length) return;

          containers.forEach(function (container) {
            var tabs = container.querySelectorAll('.gws-tabs-tab');
            var panels = container.querySelectorAll('.gws-tabs-panel');
            tabs.forEach(function (tab) {
              tab.addEventListener('click', function () {
                var index = tab.getAttribute('data-gws-tab-index');
                tabs.forEach(function (t) { t.classList.remove('is-active'); t.setAttribute('aria-selected', 'false'); });
                panels.forEach(function (p) { p.classList.remove('is-active'); });
                tab.classList.add('is-active');
                tab.setAttribute('aria-selected', 'true');
                var panel = container.querySelector('[data-gws-tab-panel="' + index + '"]');
                if (panel) panel.classList.add('is-active');
              });
            });
          });
        })();
        </script>
        """;

    // Workstream C, Tier 3 (pricing table monthly/yearly toggle) - same no-op-when-absent
    // pattern as the other runtime scripts. Both prices already live in the DOM as data
    // attributes (see RenderPricingTable), so this only ever swaps which one is displayed.
    public static string BuildPricingTableRuntimeScript() => """
        <script>
        (function () {
          var tables = document.querySelectorAll('[data-gws-pricing-table]');
          if (!tables.length) return;

          tables.forEach(function (table) {
            var buttons = table.querySelectorAll('[data-gws-pricing-period]');
            var values = table.querySelectorAll('[data-gws-pricing-value]');
            buttons.forEach(function (btn) {
              btn.addEventListener('click', function () {
                var period = btn.getAttribute('data-gws-pricing-period');
                buttons.forEach(function (b) { b.classList.remove('is-active'); });
                btn.classList.add('is-active');
                values.forEach(function (v) {
                  var val = v.getAttribute('data-' + period);
                  if (val) v.textContent = val;
                });
              });
            });
          });
        })();
        </script>
        """;

    // Workstream C, Tier 3 (gallery/image grid) - same no-op-when-absent pattern as the other
    // runtime scripts. Builds exactly one shared lightbox overlay for the whole page (not one
    // per gallery widget) and repoints its image/caption at whichever thumbnail was clicked.
    public static string BuildGalleryRuntimeScript() => """
        <script>
        (function () {
          var triggers = document.querySelectorAll('[data-gws-gallery-trigger]');
          if (!triggers.length) return;

          var overlay = document.createElement('div');
          overlay.className = 'gws-gallery-lightbox';
          overlay.setAttribute('role', 'dialog');
          overlay.setAttribute('aria-modal', 'true');
          overlay.innerHTML =
            '<button type="button" class="gws-gallery-lightbox-close" aria-label="Close">&times;</button>' +
            '<img class="gws-gallery-lightbox-img" alt="" />' +
            '<div class="gws-gallery-lightbox-caption"></div>';
          document.body.appendChild(overlay);
          var img = overlay.querySelector('.gws-gallery-lightbox-img');
          var caption = overlay.querySelector('.gws-gallery-lightbox-caption');

          function close() { overlay.classList.remove('is-open'); }
          function open(src, captionText) {
            img.src = src;
            caption.textContent = captionText || '';
            caption.hidden = !captionText;
            overlay.classList.add('is-open');
          }

          triggers.forEach(function (trigger) {
            trigger.addEventListener('click', function () {
              open(trigger.getAttribute('data-gws-gallery-src'), trigger.getAttribute('data-gws-gallery-caption'));
            });
          });
          overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });
          overlay.querySelector('.gws-gallery-lightbox-close').addEventListener('click', close);
          document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
        })();
        </script>
        """;

    // Workstream C, Tier 3 (carousel / testimonial slider) - same no-op-when-absent pattern as
    // the other runtime scripts. Shared by both widget types since they render into the
    // identical DOM shell (see WrapCarouselSlides) - one script, two content shapes.
    public static string BuildCarouselRuntimeScript() => """
        <script>
        (function () {
          var carousels = document.querySelectorAll('[data-gws-carousel]');
          if (!carousels.length) return;

          carousels.forEach(function (carousel) {
            var slides = carousel.querySelectorAll('.gws-carousel-slide');
            var dots = carousel.querySelectorAll('.gws-carousel-dot');
            if (slides.length < 2) return;
            var current = 0;

            function show(index) {
              current = (index + slides.length) % slides.length;
              slides.forEach(function (s, i) { s.classList.toggle('is-active', i === current); });
              dots.forEach(function (d, i) { d.classList.toggle('is-active', i === current); });
            }

            var prevBtn = carousel.querySelector('.gws-carousel-prev');
            var nextBtn = carousel.querySelector('.gws-carousel-next');
            if (prevBtn) prevBtn.addEventListener('click', function () { show(current - 1); });
            if (nextBtn) nextBtn.addEventListener('click', function () { show(current + 1); });
            dots.forEach(function (dot, i) { dot.addEventListener('click', function () { show(i); }); });
          });
        })();
        </script>
        """;

    // Workstream C, Tier 3 (portfolio/project grid) - same no-op-when-absent pattern as the
    // other runtime scripts. Builds exactly ONE shared detail overlay for the whole page (same
    // approach as BuildGalleryRuntimeScript's lightbox) and repoints it at whichever item was
    // clicked; category filtering is scoped per-widget since a page could carry more than one
    // portfolio grid with different category sets.
    public static string BuildPortfolioRuntimeScript() => """
        <script>
        (function () {
          var portfolios = document.querySelectorAll('[data-gws-portfolio]');
          if (!portfolios.length) return;

          var overlay = document.createElement('div');
          overlay.className = 'gws-portfolio-lightbox';
          overlay.setAttribute('role', 'dialog');
          overlay.setAttribute('aria-modal', 'true');
          overlay.innerHTML =
            '<div class="gws-portfolio-lightbox-panel">' +
              '<button type="button" class="gws-portfolio-lightbox-close" aria-label="Close">&times;</button>' +
              '<img class="gws-portfolio-lightbox-img" alt="" />' +
              '<div class="gws-portfolio-lightbox-body">' +
                '<div class="gws-portfolio-lightbox-category"></div>' +
                '<h3 class="gws-portfolio-lightbox-title"></h3>' +
                '<p class="gws-portfolio-lightbox-description"></p>' +
                '<div class="gws-portfolio-lightbox-tags"></div>' +
                '<a class="gws-portfolio-lightbox-link btn btn-primary" target="_blank" rel="noopener noreferrer">Visit project</a>' +
              '</div>' +
            '</div>';
          document.body.appendChild(overlay);
          var img = overlay.querySelector('.gws-portfolio-lightbox-img');
          var categoryEl = overlay.querySelector('.gws-portfolio-lightbox-category');
          var titleEl = overlay.querySelector('.gws-portfolio-lightbox-title');
          var descriptionEl = overlay.querySelector('.gws-portfolio-lightbox-description');
          var tagsEl = overlay.querySelector('.gws-portfolio-lightbox-tags');
          var linkEl = overlay.querySelector('.gws-portfolio-lightbox-link');

          function close() { overlay.classList.remove('is-open'); }
          function open(trigger) {
            img.src = trigger.getAttribute('data-gws-portfolio-image') || '';
            var title = trigger.getAttribute('data-gws-portfolio-title') || '';
            img.alt = title;
            titleEl.textContent = title;
            var category = trigger.getAttribute('data-gws-portfolio-category') || '';
            categoryEl.textContent = category;
            categoryEl.hidden = !category;
            var description = trigger.getAttribute('data-gws-portfolio-description') || '';
            descriptionEl.textContent = description;
            descriptionEl.hidden = !description;
            var tags = (trigger.getAttribute('data-gws-portfolio-tags') || '').split(',').map(function (t) { return t.trim(); }).filter(Boolean);
            tagsEl.innerHTML = '';
            tags.forEach(function (tag) {
              var pill = document.createElement('span');
              pill.className = 'gws-portfolio-lightbox-tag';
              pill.textContent = tag;
              tagsEl.appendChild(pill);
            });
            var link = trigger.getAttribute('data-gws-portfolio-link') || '';
            linkEl.href = link;
            linkEl.hidden = !link;
            overlay.classList.add('is-open');
          }

          document.querySelectorAll('[data-gws-portfolio-trigger]').forEach(function (trigger) {
            trigger.addEventListener('click', function () { open(trigger); });
          });
          overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });
          overlay.querySelector('.gws-portfolio-lightbox-close').addEventListener('click', close);
          document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });

          portfolios.forEach(function (portfolio) {
            var filters = portfolio.querySelectorAll('[data-gws-portfolio-filter]');
            var items = portfolio.querySelectorAll('[data-gws-portfolio-trigger]');
            filters.forEach(function (filterBtn) {
              filterBtn.addEventListener('click', function () {
                var filter = filterBtn.getAttribute('data-gws-portfolio-filter');
                filters.forEach(function (f) { f.classList.remove('is-active'); });
                filterBtn.classList.add('is-active');
                items.forEach(function (item) {
                  var category = item.getAttribute('data-gws-portfolio-category');
                  item.style.display = (filter === 'all' || category === filter) ? '' : 'none';
                });
              });
            });
          });
        })();
        </script>
        """;

    private static string RenderWidget(LayoutWidget widget, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles, IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>> relatedPostsByAnchorSlug, DesignTokenSet? tokens = null, string? turnstileSiteKey = null)
    {
        var p = widget.Props;
        return widget.WidgetType switch
        {
            "hero" => RenderHero(p, editMode),
            "heading" => $"""<{Tag(p)} class="gws-heading gws-align-{Html(Align(p))}"{InlineEditAttrs(editMode, "text")}>{Html(Get(p, "text"))}</{Tag(p)}>""",
            "paragraph" => $"""<div class="gws-paragraph gws-align-{Html(Align(p))}"{InlineRichAttrs(editMode, "text", Get(p, "text"))}>{Markdown.ToHtml(Get(p, "text"), MarkdownPipeline)}</div>""",
            // Same trust boundary as blog articles: only authenticated Contributor/Author/
            // Admin roles can edit Canvas widgets, so rendering Markdown -> HTML here (rather
            // than HTML-encoding it, which would show raw asterisks/brackets) is consistent
            // with how ArticleMarkdownRenderer already treats admin-authored content.
            // Deliberately NOT inline-contenteditable (see BuildEditModeScript's caller) -
            // this prop is Markdown, contenteditable produces HTML, and reconciling
            // HTML-from-contenteditable back into Markdown is a lossy conversion for no
            // real gain. Stays click-to-select -> edit in the Inspector's Markdown textarea.
            "richtext" => $"""<div class="gws-richtext"{InlineRichAttrs(editMode, "content", Get(p, "content"))}>{Markdown.ToHtml(Get(p, "content"), MarkdownPipeline)}</div>""",
            "button" => $"""
                <div class="gws-button-wrap gws-align-{Html(Align(p))}">
                  <a href="{Html(HrefOrHash(Get(p, "href")))}" class="btn btn-{Html(Get(p, "variant", "primary"))}"{OpenInNewTabAttrs(p)}{InlineEditAttrs(editMode, "label")}>{Html(Get(p, "label"))}</a>
                </div>
                """,
            "image" => HasValue(p, "src")
                ? $"""
                    <div class="gws-image gws-image-{Html(Get(p, "width", "full"))}">
                      <img src="{Html(Get(p, "src"))}" alt="{Html(Get(p, "alt"))}" />
                      {(HasValue(p, "caption") ? $"""<p class="gws-image-caption"{InlineEditAttrs(editMode, "caption")}>{Html(Get(p, "caption"))}</p>""" : "")}
                    </div>
                    """
                : string.Empty,
            "card" => $"""
                <div class="gws-card">
                  {(HasValue(p, "imageSrc") ? $"""<img src="{Html(Get(p, "imageSrc"))}" alt="" class="gws-card-img" />""" : "")}
                  <div class="gws-card-body">
                    <h3 class="gws-card-title"{InlineEditAttrs(editMode, "title")}>{Html(Get(p, "title"))}</h3>
                    <div class="gws-card-text"{InlineRichAttrs(editMode, "body", Get(p, "body"))}>{Markdown.ToHtml(Get(p, "body"), MarkdownPipeline)}</div>
                    {(HasValue(p, "link") ? $"""<a href="{Html(HrefOrHash(Get(p, "link")))}" class="btn btn-sm btn-outline-primary">Read more</a>""" : "")}
                  </div>
                </div>
                """,
            "testimonial" => $"""
                <blockquote class="gws-testimonial">
                  <div class="gws-testimonial-quote"{InlineRichAttrs(editMode, "quote", Get(p, "quote"))}>{Markdown.ToHtml(Get(p, "quote"), MarkdownPipeline)}</div>
                  <footer class="gws-testimonial-author">
                    <span class="gws-testimonial-name"{InlineEditAttrs(editMode, "authorName")}>{Html(Get(p, "authorName"))}</span>
                    {(HasValue(p, "authorRole") ? $"""<span class="gws-testimonial-role"{InlineEditAttrs(editMode, "authorRole")}>{Html(Get(p, "authorRole"))}</span>""" : "")}
                  </footer>
                </blockquote>
                """,
            "accordion" => RenderAccordion(p, editMode),
            "spacer" => $"""<div class="gws-spacer" style="height:{GetInt(p, "height", 48)}px"></div>""",
            "divider" => $"""<hr class="gws-divider gws-divider-{Html(Get(p, "style", "solid"))}" />""",
            "html" => Get(p, "content"),
            "form" => RenderForm(p, siteSlug, pageSlug, editMode, turnstileSiteKey),
            "posts-grid" => RenderPostsGrid(p, articles),
            "related-posts" => RenderRelatedPosts(p, relatedPostsByAnchorSlug, editMode),
            "author-box" => RenderAuthorBox(p, editMode),
            "callout" => RenderCallout(p, editMode),
            "table-of-contents" => RenderTableOfContents(p),
            "reading-progress" => RenderReadingProgress(p, tokens),
            "booking" => RenderBooking(p, editMode),
            "stats" => RenderStats(p, editMode),
            "cta-banner" => RenderCtaBanner(p, editMode),
            "email-signup" => RenderEmailSignup(p, pageSlug, editMode),
            "map" => RenderMap(p, editMode),
            "site-logo" => RenderSiteLogo(p),
            "nav-menu" => RenderNavMenu(p),
            "copyright" => RenderCopyright(p),
            "team-grid" => RenderTeamGrid(p, editMode),
            "logo-cloud" => RenderLogoCloud(p),
            "process-steps" => RenderProcessSteps(p, editMode),
            "tabs" => RenderTabs(p, editMode),
            "pricing-table" => RenderPricingTable(p, editMode),
            "gallery" => RenderGallery(p, editMode),
            "carousel" => RenderCarousel(p, editMode),
            "testimonial-slider" => RenderTestimonialSlider(p, editMode),
            "portfolio-grid" => RenderPortfolioGrid(p),
            "case-study" => RenderCaseStudy(p, editMode),
            _ => string.Empty
        };
    }

    // Static content widget - no DB access, no live data - a bio card for an article's or
    // page's author. Each social link Prop is independently optional (empty = hidden),
    // matching the hero widget's cta2Label/cta2Href convention above.
    private static string RenderAuthorBox(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var name = Get(p, "name");
        var socialLinks = new StringBuilder();
        AppendSocialLink(socialLinks, Get(p, "websiteUrl"), "bi-globe2", "Website", "gws-author-box-social-link");
        AppendSocialLink(socialLinks, Get(p, "twitterUrl"), "bi-twitter-x", "Twitter/X", "gws-author-box-social-link");
        AppendSocialLink(socialLinks, Get(p, "linkedinUrl"), "bi-linkedin", "LinkedIn", "gws-author-box-social-link");
        if (HasValue(p, "emailAddress"))
        {
            socialLinks.Append($"""<a href="mailto:{Html(Get(p, "emailAddress"))}" class="gws-author-box-social-link" aria-label="Email"><i class="bi bi-envelope" aria-hidden="true"></i></a>""");
        }

        return $"""
            <div class="gws-author-box">
              {(HasValue(p, "avatarUrl") ? $"""<img src="{Html(Get(p, "avatarUrl"))}" alt="{Html(name)}" class="gws-author-box-avatar" />""" : "")}
              <div class="gws-author-box-body">
                <div class="gws-author-box-name"{InlineEditAttrs(editMode, "name")}>{Html(name)}</div>
                {(HasValue(p, "roleOrTitle") ? $"""<div class="gws-author-box-role"{InlineEditAttrs(editMode, "roleOrTitle")}>{Html(Get(p, "roleOrTitle"))}</div>""" : "")}
                {(HasValue(p, "bio") ? $"""<div class="gws-author-box-bio"{InlineRichAttrs(editMode, "bio", Get(p, "bio"))}>{Markdown.ToHtml(Get(p, "bio"), MarkdownPipeline)}</div>""" : "")}
                {(socialLinks.Length > 0 ? $"""<div class="gws-author-box-social">{socialLinks}</div>""" : "")}
              </div>
            </div>
            """;
    }

    // Shared by author-box and team-grid - both render a row of independently-optional social
    // links (empty URL = hidden) off the same small set of Props. cssClass is passed explicitly
    // rather than hardcoded so each widget's CSS stays self-contained instead of the two widgets
    // fighting over one shared class's sizing/spacing assumptions.
    private static void AppendSocialLink(StringBuilder sb, string url, string iconClass, string label, string cssClass)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        sb.Append($"""<a href="{Html(HrefOrHash(url))}" class="{cssClass}" target="_blank" rel="noopener noreferrer" aria-label="{Html(label)}"><i class="bi {iconClass}" aria-hidden="true"></i></a>""");
    }

    // Header/footer builder widgets. The logo and menus are site data the renderer doesn't have,
    // so these emit empty placeholders that PublicSiteHtmlRenderer.FillSiteChrome fills in on
    // every render path (live site, editor canvas, export) from Appearance > Menus and the site's
    // logo. Copyright needs nothing but the current year.
    private static string RenderSiteLogo(IReadOnlyDictionary<string, string> p)
    {
        var height = Math.Clamp(int.TryParse(Get(p, "height", "36"), out var h) ? h : 36, 16, 120);
        var showName = Get(p, "showName", "true") != "false" ? "1" : "0";
        return $"""<div class="gws-site-logo-wrap gws-align-{Html(Align(p))}"><a href="/" class="gws-site-logo" data-gws-site-logo="{showName}" style="--gws-logo-height:{height}px"></a></div>""";
    }

    private static string RenderNavMenu(IReadOnlyDictionary<string, string> p)
    {
        var menu = Get(p, "menu", "header") == "footer" ? "footer" : "header";
        var direction = Get(p, "direction", "horizontal") == "vertical" ? "vertical" : "horizontal";
        return $"""<nav class="gws-nav-menu gws-nav-menu-{direction} gws-align-{Html(Align(p))}" aria-label="{(menu == "footer" ? "Footer" : "Main")}" data-gws-nav-menu="{menu}"></nav>""";
    }

    private static string RenderCopyright(IReadOnlyDictionary<string, string> p)
    {
        var holder = Get(p, "holder");
        var text = Get(p, "text");
        var admin = Get(p, "showAdminLink", "false") == "true" ? """ <a href="/admin" class="footer-admin-link">admin</a>""" : "";
        var line = $"&copy; {DateTimeOffset.UtcNow.Year}{(string.IsNullOrWhiteSpace(holder) ? "" : " " + Html(holder))}{(string.IsNullOrWhiteSpace(text) ? "" : ". " + Html(text))}";
        return $"""<p class="gws-copyright gws-align-{Html(Align(p))}">{line}{admin}</p>""";
    }

    // Location map - OpenStreetMap's own embeddable map (free, no API key, no tile requests of
    // ours), centred on coordinates the editor geocodes once from the address (see
    // CmsBuilderEditor's "Find on map"). frame-src allows www.openstreetmap.org for exactly this.
    private static string RenderMap(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var address = Get(p, "address");
        var heading = Get(p, "heading");
        var height = Math.Clamp(int.TryParse(Get(p, "height", "360"), out var h) ? h : 360, 200, 800);
        if (!TryGetCoordinates(p, out var lat, out var lng))
        {
            return editMode
                ? $"""<div class="gws-map gws-map-empty" style="min-height:{height}px"><div class="gws-email-signup-warning">Enter an address in the inspector and click <strong>Find on map</strong>. The map stays hidden on the live site until it has a location.</div></div>"""
                : string.Empty;
        }

        var zoom = Math.Clamp(int.TryParse(Get(p, "zoom", "15"), out var z) ? z : 15, 3, 18);
        // OSM's embed takes a bounding box rather than a zoom level: half-spans that roughly match
        // the requested zoom at a ~2:1 map shape.
        var lngSpan = 360.0 / Math.Pow(2, zoom) * 1.6;
        var latSpan = lngSpan * 0.45 * Math.Cos(lat * Math.PI / 180);
        string F(double value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        var bbox = $"{F(lng - lngSpan)},{F(lat - latSpan)},{F(lng + lngSpan)},{F(lat + latSpan)}";
        var src = $"https://www.openstreetmap.org/export/embed.html?bbox={Uri.EscapeDataString(bbox)}&layer=mapnik&marker={F(lat)}%2C{F(lng)}";
        var directions = $"https://www.openstreetmap.org/directions?to={F(lat)}%2C{F(lng)}";
        var label = string.IsNullOrWhiteSpace(address) ? "Location map" : $"Map of {address}";
        var showDirections = Get(p, "showDirections", "true") != "false";
        // In the editor the frame mustn't swallow the click that selects the widget.
        var frameStyle = editMode ? "pointer-events:none;" : string.Empty;
        return $"""
            <div class="gws-map">
              {(string.IsNullOrWhiteSpace(heading) ? "" : $"""<h2 class="gws-map-heading"{InlineEditAttrs(editMode, "heading")}>{Html(heading)}</h2>""")}
              <iframe class="gws-map-frame" src="{Html(src)}" title="{Html(label)}" loading="lazy" referrerpolicy="no-referrer-when-downgrade" style="height:{height}px;{frameStyle}"></iframe>
              <div class="gws-map-footer">
                <span class="gws-map-address">{Html(address)}</span>
                {(showDirections ? $"""<a class="gws-map-directions" href="{Html(directions)}" target="_blank" rel="noopener">Get directions</a>""" : "")}
              </div>
            </div>
            """;
    }

    private static bool TryGetCoordinates(IReadOnlyDictionary<string, string> p, out double lat, out double lng)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var ok = double.TryParse(Get(p, "lat"), System.Globalization.NumberStyles.Float, culture, out lat)
            & double.TryParse(Get(p, "lng"), System.Globalization.NumberStyles.Float, culture, out lng);
        return ok && lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
    }

    // Workstream C, Tier 2 (CTA banner, promoted from a CmsSectionTemplates composition of 3
    // separate widgets to one first-class type) - a centered/left headline, optional body copy,
    // and one optional button, all as a single insertable/reusable widget instead of 3 that have
    // to be selected, styled, and moved together. Existing pages built from the OLD 3-widget
    // composition are completely unaffected - those heading/paragraph/button widgets still
    // render exactly as they always have; only NEW section-template insertions use this type now.
    // "Email signup" widget: subscribes a visitor to the chosen article-alert campaign (double
    // opt-in - see ArticleAlertService/ArticleAlertEndpoints). wwwroot/js/email-signup.js submits it
    // in place and adds Turnstile when configured; without JS it still posts and gets a full page.
    private static string RenderEmailSignup(IReadOnlyDictionary<string, string> p, string pageSlug, bool editMode)
    {
        var campaignId = Guid.TryParse(Get(p, "campaignId"), out var parsed) ? parsed : Guid.Empty;
        if (campaignId == Guid.Empty && !editMode) return string.Empty; // nothing to subscribe to yet
        var align = Get(p, "align", "center") == "left" ? "left" : "center";
        var consent = Get(p, "consentText", "No spam. Unsubscribe any time.");
        var path = "/" + (pageSlug ?? string.Empty).Trim('/');
        var disabled = editMode ? " disabled" : string.Empty;
        var nameField = Get(p, "showFirstName", "true") == "false"
            ? string.Empty
            : $"""<input class="gws-email-signup-input gws-email-signup-name" type="text" name="firstName" autocomplete="given-name" maxlength="80" placeholder="{Html(Get(p, "firstNamePlaceholder", "First name (optional)"))}" aria-label="First name (optional)"{disabled} />""";
        var warning = editMode && campaignId == Guid.Empty
            ? """<div class="gws-email-signup-warning">Choose which email campaign this signup feeds - select the widget, then use <strong>Campaign</strong> in the inspector. It stays hidden on the live site until you do.</div>"""
            : string.Empty;

        return $"""
            <div class="gws-email-signup gws-align-{Html(align)}">
              {warning}
              {(HasValue(p, "heading") ? $"""<h2 class="gws-email-signup-heading"{InlineEditAttrs(editMode, "heading")}>{Html(Get(p, "heading"))}</h2>""" : "")}
              {(HasValue(p, "description") ? $"""<div class="gws-email-signup-body"{InlineRichAttrs(editMode, "description", Get(p, "description"))}>{Markdown.ToHtml(Get(p, "description"), MarkdownPipeline)}</div>""" : "")}
              <form class="gws-email-signup-form" method="post" action="/campaigns/{campaignId}/subscribe" data-gws-email-signup-form
                    data-success="{Html(Get(p, "successMessage", "Almost done - check your inbox and click the link to confirm."))}">
                <input type="hidden" name="_path" value="{Html(path)}" />
                <input type="hidden" name="_consent" value="{Html(consent)}" />
                <input type="text" name="_hp" class="gws-form-honeypot" tabindex="-1" autocomplete="off" aria-hidden="true" />
                <div class="gws-email-signup-fields">
                  {nameField}
                  <input class="gws-email-signup-input" type="email" name="email" required autocomplete="email" maxlength="254" placeholder="{Html(Get(p, "emailPlaceholder", "you@example.com"))}" aria-label="Email address"{disabled} />
                  <button type="submit" class="btn btn-primary gws-email-signup-submit"{disabled}>{Html(Get(p, "buttonLabel", "Subscribe"))}</button>
                </div>
                <div class="gws-email-signup-verification"></div>
                {(string.IsNullOrWhiteSpace(consent) ? "" : $"""<p class="gws-email-signup-consent">{Html(consent)}</p>""")}
                <p class="gws-email-signup-status" role="status" aria-live="polite"></p>
              </form>
            </div>
            """;
    }

    private static string RenderCtaBanner(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var align = Get(p, "align", "center") == "left" ? "left" : "center";
        var variant = Get(p, "buttonVariant", "primary") == "outline-primary" ? "outline-primary" : "primary";

        return $"""
            <div class="gws-cta-banner gws-align-{Html(align)}">
              <h2 class="gws-cta-banner-headline"{InlineEditAttrs(editMode, "headline")}>{Html(Get(p, "headline"))}</h2>
              {(HasValue(p, "body") ? $"""<div class="gws-cta-banner-body"{InlineRichAttrs(editMode, "body", Get(p, "body"))}>{Markdown.ToHtml(Get(p, "body"), MarkdownPipeline)}</div>""" : "")}
              {(HasValue(p, "buttonLabel") ? $"""<div class="gws-cta-banner-actions"><a href="{Html(HrefOrHash(Get(p, "buttonHref")))}" class="btn btn-{Html(variant)}"{InlineEditAttrs(editMode, "buttonLabel")}>{Html(Get(p, "buttonLabel"))}</a></div>""" : "")}
            </div>
            """;
    }

    // Workstream C, Tier 2 (team/people grid, promoted from a CmsSectionTemplates composition of
    // N separate Card widgets to one first-class type) - same non-breaking promotion note as
    // RenderCtaBanner above: existing pages built from the old per-person Card widgets are
    // unaffected. Deliberately no bio field (the original template was photo+name+role only) -
    // social links are the one addition, reusing AppendSocialLink at near-zero marginal cost.
    private static string RenderTeamGrid(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder("""<div class="gws-team-grid">""");
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var name = item["name"]?.GetValue<string>() ?? string.Empty;
                var role = item["role"]?.GetValue<string>() ?? string.Empty;
                var photoUrl = item["photoUrl"]?.GetValue<string>() ?? string.Empty;
                var linkedinUrl = item["linkedinUrl"]?.GetValue<string>() ?? string.Empty;
                var twitterUrl = item["twitterUrl"]?.GetValue<string>() ?? string.Empty;
                var emailAddress = item["emailAddress"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var socialLinks = new StringBuilder();
                AppendSocialLink(socialLinks, twitterUrl, "bi-twitter-x", "Twitter/X", "gws-team-grid-social-link");
                AppendSocialLink(socialLinks, linkedinUrl, "bi-linkedin", "LinkedIn", "gws-team-grid-social-link");
                if (!string.IsNullOrWhiteSpace(emailAddress))
                {
                    socialLinks.Append($"""<a href="mailto:{Html(emailAddress)}" class="gws-team-grid-social-link" aria-label="Email"><i class="bi bi-envelope" aria-hidden="true"></i></a>""");
                }

                sb.Append($"""
                    <div class="gws-team-grid-item">
                      {(!string.IsNullOrWhiteSpace(photoUrl) ? $"""<img src="{Html(photoUrl)}" alt="{Html(name)}" class="gws-team-grid-photo" />""" : "")}
                      <div class="gws-team-grid-name"{InlineEditAttrs(editMode, $"itemsJson[{index}].name")}>{Html(name)}</div>
                      {(!string.IsNullOrWhiteSpace(role) ? $"""<div class="gws-team-grid-role"{InlineEditAttrs(editMode, $"itemsJson[{index}].role")}>{Html(role)}</div>""" : "")}
                      {(socialLinks.Length > 0 ? $"""<div class="gws-team-grid-social">{socialLinks}</div>""" : "")}
                    </div>
                    """);
            }
            sb.Append("</div>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 2 (logo cloud / integration showcase) - a static row of partner/client/
    // integration logos, each independently optionally wrapped in a link. "grayscale" defaults
    // to true (the common pattern of desaturating logos until hover) but is opt-out, not opt-in,
    // since a genuinely colorful logo row is a real style choice some sites want.
    private static string RenderLogoCloud(IReadOnlyDictionary<string, string> p)
    {
        var itemsJson = Get(p, "itemsJson");
        var grayscaleClass = Get(p, "grayscale", "true") == "false" ? "" : " gws-logo-cloud-grayscale";
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder($"""<div class="gws-logo-cloud{grayscaleClass}">""");
            foreach (var item in node.OfType<JsonObject>())
            {
                var logoUrl = item["logoUrl"]?.GetValue<string>() ?? string.Empty;
                var name = item["name"]?.GetValue<string>() ?? string.Empty;
                var linkUrl = item["linkUrl"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(logoUrl)) continue;

                var img = $"""<img src="{Html(logoUrl)}" alt="{Html(name)}" class="gws-logo-cloud-img" loading="lazy" />""";
                sb.Append(string.IsNullOrWhiteSpace(linkUrl)
                    ? $"""<div class="gws-logo-cloud-item">{img}</div>"""
                    : $"""<a href="{Html(HrefOrHash(linkUrl))}" class="gws-logo-cloud-item" target="_blank" rel="noopener noreferrer">{img}</a>""");
            }
            sb.Append("</div>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 2 (process/steps widget) - the step number is derived from the item's
    // position, never stored in itemsJson - it's real sequence information (per this codebase's
    // "structure is information" convention), and deriving it means reordering/removing a step
    // can never leave a stale "3" sitting where step 2 now is.
    private static string RenderProcessSteps(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder("""<div class="gws-process-steps">""");
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var title = item["title"]?.GetValue<string>() ?? string.Empty;
                var description = item["description"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title)) continue;

                sb.Append($"""
                    <div class="gws-process-step">
                      <div class="gws-process-step-number" aria-hidden="true">{index + 1}</div>
                      <div class="gws-process-step-body">
                        <div class="gws-process-step-title"{InlineEditAttrs(editMode, $"itemsJson[{index}].title")}>{Html(title)}</div>
                        {(!string.IsNullOrWhiteSpace(description) ? $"""<div class="gws-process-step-description"{InlineEditAttrs(editMode, $"itemsJson[{index}].description")}>{Html(description)}</div>""" : "")}
                      </div>
                    </div>
                    """);
            }
            sb.Append("</div>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 2 (tabs widget) - a generic tabbed container over Markdown content
    // panels. Static shell for both the nav buttons and the panels (SEO-visible, all panels'
    // real content is in the DOM from first render, just visually hidden past the first) with
    // BuildTabsRuntimeScript doing the click-to-switch behavior - plain click listeners, no
    // IntersectionObserver/scroll involved, so (unlike the counter/scrollspy scripts) it's safe
    // to run identically in edit mode too rather than being skipped there.
    private static string RenderTabs(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var tabs = new StringBuilder();
            var panels = new StringBuilder();
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                var label = item["label"]?.GetValue<string>() ?? string.Empty;
                var content = item["content"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(label)) continue;
                index++;

                var activeClass = index == 0 ? " is-active" : "";
                tabs.Append($"""
                    <button type="button" class="gws-tabs-tab{activeClass}" role="tab" aria-selected="{(index == 0 ? "true" : "false")}" data-gws-tab-index="{index}"{InlineEditAttrs(editMode, $"itemsJson[{index}].label")}>{Html(label)}</button>
                    """);
                panels.Append($"""
                    <div class="gws-tabs-panel{activeClass}" role="tabpanel" data-gws-tab-panel="{index}"{InlineRichAttrs(editMode, $"itemsJson[{index}].content", content)}>{Markdown.ToHtml(content, MarkdownPipeline)}</div>
                    """);
            }
            if (index < 0) return string.Empty;

            return $"""
                <div class="gws-tabs" data-gws-tabs>
                  <div class="gws-tabs-nav" role="tablist">{tabs}</div>
                  <div class="gws-tabs-panels">{panels}</div>
                </div>
                """;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 3 (pricing table monthly/yearly toggle, promoted from the
    // CmsSectionTemplates "pricing-table" composition of fixed heading/button widgets to a
    // first-class type) - both prices for every plan are always in the DOM (a data attribute
    // each), so BuildPricingTableRuntimeScript just swaps which one is displayed - no
    // server round-trip, and the "wrong" price is never briefly visible while JS loads since
    // the visible text starts as whichever period is selected by default (monthly).
    private static string RenderPricingTable(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var cards = new StringBuilder();
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var name = item["name"]?.GetValue<string>() ?? string.Empty;
                var monthlyPrice = item["monthlyPrice"]?.GetValue<string>() ?? string.Empty;
                var yearlyPrice = item["yearlyPrice"]?.GetValue<string>() ?? string.Empty;
                var features = item["features"]?.GetValue<string>() ?? string.Empty;
                var ctaLabel = item["ctaLabel"]?.GetValue<string>() ?? string.Empty;
                var ctaHref = item["ctaHref"]?.GetValue<string>() ?? string.Empty;
                var highlighted = item["highlighted"]?.GetValue<bool>() ?? false;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var featureItems = features.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var featuresHtml = featureItems.Length == 0
                    ? ""
                    : $"""<ul class="gws-pricing-card-features">{string.Concat(featureItems.Select(f => $"""<li>{Html(f)}</li>"""))}</ul>""";

                cards.Append($"""
                    <div class="gws-pricing-card{(highlighted ? " gws-pricing-card-highlighted" : "")}">
                      <div class="gws-pricing-card-name"{InlineEditAttrs(editMode, $"itemsJson[{index}].name")}>{Html(name)}</div>
                      <div class="gws-pricing-card-price"><span data-gws-pricing-value data-monthly="{Html(monthlyPrice)}" data-yearly="{Html(yearlyPrice)}">{Html(monthlyPrice)}</span></div>
                      {featuresHtml}
                      {(!string.IsNullOrWhiteSpace(ctaLabel) ? $"""<a href="{Html(HrefOrHash(ctaHref))}" class="btn btn-{(highlighted ? "primary" : "outline-primary")}">{Html(ctaLabel)}</a>""" : "")}
                    </div>
                    """);
            }
            if (cards.Length == 0) return string.Empty;

            return $"""
                <div class="gws-pricing-table" data-gws-pricing-table>
                  <div class="gws-pricing-toggle" role="tablist">
                    <button type="button" class="gws-pricing-toggle-btn is-active" data-gws-pricing-period="monthly">Monthly</button>
                    <button type="button" class="gws-pricing-toggle-btn" data-gws-pricing-period="yearly">Yearly{(HasValue(p, "yearlyDiscountLabel") ? $"""<span class="gws-pricing-toggle-badge">{Html(Get(p, "yearlyDiscountLabel"))}</span>""" : "")}</button>
                  </div>
                  <div class="gws-pricing-cards">{cards}</div>
                </div>
                """;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly IReadOnlyList<string> GalleryColumnCounts = ["2", "3", "4"];

    // Workstream C, Tier 3 (gallery/image grid) - each thumbnail is a real <button> (keyboard-
    // and screen-reader-operable, unlike a bare clickable <div>) carrying its full-size src and
    // caption as data attributes; BuildGalleryRuntimeScript builds ONE shared lightbox overlay
    // per page (not one per gallery widget) and repoints it at whichever thumbnail was clicked.
    private static string RenderGallery(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        var columns = GalleryColumnCounts.Contains(Get(p, "columns", "3")) ? Get(p, "columns", "3") : "3";
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder($"""<div class="gws-gallery gws-gallery-cols-{columns}">""");
            foreach (var item in node.OfType<JsonObject>())
            {
                var imageUrl = item["imageUrl"]?.GetValue<string>() ?? string.Empty;
                var caption = item["caption"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(imageUrl)) continue;

                sb.Append($"""
                    <button type="button" class="gws-gallery-item" data-gws-gallery-trigger data-gws-gallery-src="{Html(imageUrl)}" data-gws-gallery-caption="{Html(caption)}">
                      <img src="{Html(imageUrl)}" alt="{Html(caption)}" loading="lazy" class="gws-gallery-img" />
                    </button>
                    """);
            }
            sb.Append("</div>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 3 (carousel / testimonial slider) - both widget types share this exact
    // DOM shell (slides + prev/next arrows + dot indicators) and BuildCarouselRuntimeScript's
    // single click-driven nav logic; only the per-slide inner content differs between
    // RenderCarousel and RenderTestimonialSlider below. Arrows/dots are omitted entirely for a
    // single-slide carousel - there's nothing to navigate to.
    private static string WrapCarouselSlides(List<string> slides)
    {
        if (slides.Count == 0) return string.Empty;

        var slideHtml = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            slideHtml.Append($"""<div class="gws-carousel-slide{(i == 0 ? " is-active" : "")}">{slides[i]}</div>""");
        }

        if (slides.Count == 1)
        {
            return $"""<div class="gws-carousel" data-gws-carousel><div class="gws-carousel-track">{slideHtml}</div></div>""";
        }

        var dots = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            dots.Append($"""<button type="button" class="gws-carousel-dot{(i == 0 ? " is-active" : "")}" data-gws-carousel-index="{i}" aria-label="Go to slide {i + 1}"></button>""");
        }

        return $"""
            <div class="gws-carousel" data-gws-carousel>
              <div class="gws-carousel-track">{slideHtml}</div>
              <button type="button" class="gws-carousel-arrow gws-carousel-prev" aria-label="Previous slide">&lsaquo;</button>
              <button type="button" class="gws-carousel-arrow gws-carousel-next" aria-label="Next slide">&rsaquo;</button>
              <div class="gws-carousel-dots">{dots}</div>
            </div>
            """;
    }

    private static string RenderCarousel(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var slides = new List<string>();
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var imageUrl = item["imageUrl"]?.GetValue<string>() ?? string.Empty;
                var title = item["title"]?.GetValue<string>() ?? string.Empty;
                var body = item["body"]?.GetValue<string>() ?? string.Empty;
                var buttonLabel = item["buttonLabel"]?.GetValue<string>() ?? string.Empty;
                var buttonHref = item["buttonHref"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(imageUrl)) continue;

                slides.Add($"""
                    {(!string.IsNullOrWhiteSpace(imageUrl) ? $"""<img src="{Html(imageUrl)}" alt="{Html(title)}" class="gws-carousel-img" />""" : "")}
                    <div class="gws-carousel-content">
                      {(!string.IsNullOrWhiteSpace(title) ? $"""<div class="gws-carousel-title"{InlineEditAttrs(editMode, $"itemsJson[{index}].title")}>{Html(title)}</div>""" : "")}
                      {(!string.IsNullOrWhiteSpace(body) ? $"""<div class="gws-carousel-body"{InlineRichAttrs(editMode, $"itemsJson[{index}].body", body)}>{Markdown.ToHtml(body, MarkdownPipeline)}</div>""" : "")}
                      {(!string.IsNullOrWhiteSpace(buttonLabel) ? $"""<a href="{Html(HrefOrHash(buttonHref))}" class="btn btn-primary">{Html(buttonLabel)}</a>""" : "")}
                    </div>
                    """);
            }
            return WrapCarouselSlides(slides);
        }
        catch
        {
            return string.Empty;
        }
    }

    // Reuses the identical carousel shell/script as RenderCarousel above - the interactive-
    // slider variant of the existing static "testimonial-row" section template (per the master
    // plan: "reusing the TOC/reading-progress precedent for a lightweight inline script rather
    // than a JS library dependency").
    private static string RenderTestimonialSlider(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var slides = new List<string>();
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var quote = item["quote"]?.GetValue<string>() ?? string.Empty;
                var authorName = item["authorName"]?.GetValue<string>() ?? string.Empty;
                var authorRole = item["authorRole"]?.GetValue<string>() ?? string.Empty;
                var avatarUrl = item["avatarUrl"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(quote)) continue;

                slides.Add($"""
                    <blockquote class="gws-carousel-testimonial">
                      <div class="gws-carousel-testimonial-quote"{InlineRichAttrs(editMode, $"itemsJson[{index}].quote", quote)}>{Markdown.ToHtml(quote, MarkdownPipeline)}</div>
                      <footer class="gws-carousel-testimonial-author">
                        {(!string.IsNullOrWhiteSpace(avatarUrl) ? $"""<img src="{Html(avatarUrl)}" alt="{Html(authorName)}" class="gws-carousel-testimonial-avatar" />""" : "")}
                        <div>
                          <span class="gws-carousel-testimonial-name"{InlineEditAttrs(editMode, $"itemsJson[{index}].authorName")}>{Html(authorName)}</span>
                          {(!string.IsNullOrWhiteSpace(authorRole) ? $"""<span class="gws-carousel-testimonial-role"{InlineEditAttrs(editMode, $"itemsJson[{index}].authorRole")}>{Html(authorRole)}</span>""" : "")}
                        </div>
                      </footer>
                    </blockquote>
                    """);
            }
            return WrapCarouselSlides(slides);
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 3 (portfolio/project grid) - decision: detail expands in an overlay
    // (same "expand in place" pattern as the Gallery widget's lightbox) rather than linking to a
    // real separate page per item, keeping this at the same effort tier as the other Tier 3
    // widgets instead of adding new routing/page-per-item infrastructure. Filter buttons only
    // render when there are 2+ distinct categories - filtering a single-category grid has
    // nothing to filter.
    private static string RenderPortfolioGrid(IReadOnlyDictionary<string, string> p)
    {
        var itemsJson = Get(p, "itemsJson");
        var columns = GalleryColumnCounts.Contains(Get(p, "columns", "3")) ? Get(p, "columns", "3") : "3";
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var items = new List<(string Title, string Category, string ImageUrl, string Description, string Tags, string LinkUrl)>();
            foreach (var item in node.OfType<JsonObject>())
            {
                var title = item["title"]?.GetValue<string>() ?? string.Empty;
                var imageUrl = item["imageUrl"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(imageUrl)) continue;

                items.Add((
                    title,
                    item["category"]?.GetValue<string>() ?? string.Empty,
                    imageUrl,
                    item["description"]?.GetValue<string>() ?? string.Empty,
                    item["tags"]?.GetValue<string>() ?? string.Empty,
                    item["linkUrl"]?.GetValue<string>() ?? string.Empty));
            }
            if (items.Count == 0) return string.Empty;

            var categories = items.Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var filters = new StringBuilder();
            if (categories.Count > 1)
            {
                filters.Append("""<button type="button" class="gws-portfolio-filter is-active" data-gws-portfolio-filter="all">All</button>""");
                foreach (var category in categories)
                {
                    filters.Append($"""<button type="button" class="gws-portfolio-filter" data-gws-portfolio-filter="{Html(category)}">{Html(category)}</button>""");
                }
            }

            var grid = new StringBuilder();
            foreach (var item in items)
            {
                var tagsDisplay = string.Join(", ", item.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                grid.Append($"""
                    <button type="button" class="gws-portfolio-item" data-gws-portfolio-trigger
                            data-gws-portfolio-category="{Html(item.Category)}"
                            data-gws-portfolio-title="{Html(item.Title)}"
                            data-gws-portfolio-description="{Html(item.Description)}"
                            data-gws-portfolio-image="{Html(item.ImageUrl)}"
                            data-gws-portfolio-tags="{Html(tagsDisplay)}"
                            data-gws-portfolio-link="{Html(item.LinkUrl)}">
                      <img src="{Html(item.ImageUrl)}" alt="{Html(item.Title)}" loading="lazy" class="gws-portfolio-img" />
                      <div class="gws-portfolio-item-overlay">
                        <div class="gws-portfolio-item-title">{Html(item.Title)}</div>
                        {(!string.IsNullOrWhiteSpace(item.Category) ? $"""<div class="gws-portfolio-item-category">{Html(item.Category)}</div>""" : "")}
                      </div>
                    </button>
                    """);
            }

            return $"""
                <div class="gws-portfolio" data-gws-portfolio>
                  {(filters.Length > 0 ? $"""<div class="gws-portfolio-filters" role="tablist">{filters}</div>""" : "")}
                  <div class="gws-portfolio-grid gws-portfolio-grid-cols-{columns}">{grid}</div>
                </div>
                """;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Workstream C, Tier 3 (case study block) - the heavier narrative variant of Portfolio Grid,
    // per the master plan's own framing: one project told as a structured story (a cover image,
    // an intro, then a flexible sequence of heading/body sections - Problem/Solution/Results, or
    // whatever shape a given case study needs) rather than a grid tile. Fully static - no
    // runtime script, no guard, matching author-box/callout's own "static content" pattern.
    private static string RenderCaseStudy(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var title = Get(p, "title");
        var sectionsJson = Get(p, "sectionsJson");
        var sections = new StringBuilder();
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(sectionsJson) ? "[]" : sectionsJson) as JsonArray;
            if (node is not null)
            {
                var index = -1;
                foreach (var item in node.OfType<JsonObject>())
                {
                    index++;
                    var heading = item["heading"]?.GetValue<string>() ?? string.Empty;
                    var body = item["body"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(heading) && string.IsNullOrWhiteSpace(body)) continue;

                    sections.Append($"""
                        <div class="gws-case-study-section">
                          {(!string.IsNullOrWhiteSpace(heading) ? $"""<h3 class="gws-case-study-section-heading"{InlineEditAttrs(editMode, $"sectionsJson[{index}].heading")}>{Html(heading)}</h3>""" : "")}
                          {(!string.IsNullOrWhiteSpace(body) ? $"""<div class="gws-case-study-section-body"{InlineRichAttrs(editMode, $"sectionsJson[{index}].body", body)}>{Markdown.ToHtml(body, MarkdownPipeline)}</div>""" : "")}
                        </div>
                        """);
                }
            }
        }
        catch
        {
            // Malformed sectionsJson - skip the narrative sections, still render the header/summary.
        }

        return $"""
            <div class="gws-case-study">
              {(HasValue(p, "imageUrl") ? $"""<img src="{Html(Get(p, "imageUrl"))}" alt="{Html(title)}" class="gws-case-study-cover" />""" : "")}
              <div class="gws-case-study-header">
                <h2 class="gws-case-study-title"{InlineEditAttrs(editMode, "title")}>{Html(title)}</h2>
                {(HasValue(p, "clientName") ? $"""<div class="gws-case-study-client"{InlineEditAttrs(editMode, "clientName")}>{Html(Get(p, "clientName"))}</div>""" : "")}
              </div>
              {(HasValue(p, "summary") ? $"""<div class="gws-case-study-summary"{InlineRichAttrs(editMode, "summary", Get(p, "summary"))}>{Markdown.ToHtml(Get(p, "summary"), MarkdownPipeline)}</div>""" : "")}
              {sections}
              {(HasValue(p, "externalUrl") ? $"""<a href="{Html(HrefOrHash(Get(p, "externalUrl")))}" class="btn btn-primary gws-case-study-cta" target="_blank" rel="noopener noreferrer">{Html(Get(p, "externalLabel", "Visit project"))}</a>""" : "")}
            </div>
            """;
    }

    // variant is validated against this fixed dictionary rather than interpolated directly -
    // BlocksJson is just a text column, so an unrecognized/hand-crafted value falls back to
    // "info" rather than emitting an arbitrary CSS class name.
    private static readonly IReadOnlyDictionary<string, (string CssClass, string Icon)> CalloutVariants = new Dictionary<string, (string, string)>
    {
        ["info"] = ("gws-callout-info", "bi-info-circle-fill"),
        ["success"] = ("gws-callout-success", "bi-check-circle-fill"),
        ["warning"] = ("gws-callout-warning", "bi-exclamation-triangle-fill"),
        ["danger"] = ("gws-callout-danger", "bi-x-octagon-fill"),
        ["note"] = ("gws-callout-note", "bi-sticky-fill"),
    };

    private static string RenderCallout(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var (cssClass, icon) = CalloutVariants.TryGetValue(Get(p, "variant", "info"), out var variant) ? variant : CalloutVariants["info"];
        var showIcon = Get(p, "showIcon", "true") == "true";

        return $"""
            <div class="gws-callout {cssClass}">
              {(showIcon ? $"""<i class="bi {icon} gws-callout-icon" aria-hidden="true"></i>""" : "")}
              <div class="gws-callout-body">
                {(HasValue(p, "title") ? $"""<div class="gws-callout-title"{InlineEditAttrs(editMode, "title")}>{Html(Get(p, "title"))}</div>""" : "")}
                <div class="gws-callout-text"{InlineRichAttrs(editMode, "body", Get(p, "body"))}>{Markdown.ToHtml(Get(p, "body"), MarkdownPipeline)}</div>
              </div>
            </div>
            """;
    }

    private static readonly IReadOnlyList<string> HeadingLevels = ["h2", "h3", "h4"];

    private static string HeadingLevel(IReadOnlyDictionary<string, string> p, string key, string fallback) =>
        HeadingLevels.Contains(Get(p, key, fallback)) ? Get(p, key, fallback) : fallback;

    // Phase 4 (JS-requiring widgets) - static shell only, matching decision 4 in the master
    // plan ("client-side DOM scan, not server-side sibling-widget awareness"). The renderer has
    // no idea what headings actually exist on the rendered page - BuildTableOfContentsRuntimeScript
    // (injected once per page, same precedent as BuildInteractionRuntimeScript) scans the DOM at
    // load time and fills the empty <ol> in here.
    private static string RenderTableOfContents(IReadOnlyDictionary<string, string> p)
    {
        var minLevel = HeadingLevel(p, "minLevel", "h2");
        var maxLevel = HeadingLevel(p, "maxLevel", "h3");
        var showNumbers = Get(p, "showNumbers", "false") == "true";
        var config = $$"""{"minLevel":"{{minLevel}}","maxLevel":"{{maxLevel}}","showNumbers":{{(showNumbers ? "true" : "false")}}}""";

        return $"""
            <nav class="gws-toc" data-gws-toc="{Html(config)}" aria-label="Table of contents">
              <div class="gws-toc-title">{Html(Get(p, "title", "On This Page"))}</div>
              <ol class="gws-toc-list"></ol>
            </nav>
            """;
    }

    // Phase 4 (JS-requiring widgets) - static shell only; BuildReadingProgressRuntimeScript
    // sizes the fill bar on scroll. colorToken takes precedence over the raw color, same
    // precedence rule as WidgetStyle.ToInlineStyle's TextColorToken/TextColor pair.
    private static string RenderReadingProgress(IReadOnlyDictionary<string, string> p, DesignTokenSet? tokens)
    {
        var color = WidgetStyle.ResolveColor(Get(p, "colorToken"), Get(p, "color"), tokens);
        var height = Math.Clamp(GetInt(p, "height", 4), 1, 24);
        var position = Get(p, "position", "top") == "bottom" ? "bottom" : "top";
        var colorStyle = string.IsNullOrWhiteSpace(color) ? "" : $"--gws-reading-progress-color:{Html(color)};";

        return $"""
            <div class="gws-reading-progress gws-reading-progress-{Html(position)}" data-gws-reading-progress style="{colorStyle}--gws-reading-progress-height:{height}px">
              <div class="gws-reading-progress-bar"></div>
            </div>
            """;
    }

    // Workstream C, Tier 1 (booking/scheduling embed) - deliberately NOT a data-fetching widget
    // like posts-grid/related-posts: it embeds the app's own already-public /book/{slug} page
    // (Components/Pages/Booking/BookingPublic.razor) via a same-origin <iframe> rather than
    // re-rendering booking-type/availability data here, so this stays true to the renderer's
    // "zero DB access of its own" contract with no Program.cs wiring at all. bookingTypeSlug is
    // trusted admin input (same trust boundary as every other widget Prop), so it goes straight
    // into the iframe src path segment - Uri.EscapeDataString still guards against it breaking
    // out of the src="" attribute via a stray quote/space.
    private static string RenderBooking(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var slug = Get(p, "bookingTypeSlug");
        if (string.IsNullOrWhiteSpace(slug))
        {
            return editMode
                ? """<div class="gws-booking-embed-placeholder">Pick a booking type in the Inspector.</div>"""
                : string.Empty;
        }

        var height = Math.Clamp(GetInt(p, "height", 720), 300, 2000);
        var title = Get(p, "title", "Book a time");

        return $"""
            <div class="gws-booking-embed">
              {(HasValue(p, "title") ? $"""<h3 class="gws-booking-embed-title"{InlineEditAttrs(editMode, "title")}>{Html(title)}</h3>""" : "")}
              <iframe class="gws-booking-embed-frame" src="/book/{Html(Uri.EscapeDataString(slug))}" style="height:{height}px" loading="lazy" title="{Html(title)}"></iframe>
            </div>
            """;
    }

    // WordPress "loop"-equivalent: a live grid of the most recently published Articles,
    // not a static block - articles is whatever the caller (Program.cs) fetched for this
    // request, already filtered to publicly-visible ones and ordered newest-first.
    // Phase 2 (Blog Block Library) - "layout" picks one of 4 presentations over the identical
    // underlying data/guard logic, rather than 4 separate widget types, matching the existing
    // button.variant/divider.style convention of style-variants-as-a-prop.
    private static string RenderPostsGrid(IReadOnlyDictionary<string, string> p, IReadOnlyList<PublicArticleSummary> articles)
    {
        var count = Math.Clamp(GetInt(p, "count", 3), 1, 12);
        var columns = Get(p, "columns", "3");
        var showImage = Get(p, "showImage", "true") == "true";
        var showExcerpt = Get(p, "showExcerpt", "true") == "true";
        var showDate = Get(p, "showDate", "true") == "true";
        var ctaLabel = Get(p, "ctaLabel", "Read More");

        var items = articles.Take(count).ToList();
        if (items.Count == 0)
        {
            return """<div class="gws-posts-grid-empty">No published posts yet.</div>""";
        }

        return Get(p, "layout", "grid") switch
        {
            "list" => RenderPostsGridList(items, showImage, showExcerpt, showDate, ctaLabel),
            "classic" => RenderPostsGridClassic(items, showImage, showExcerpt, showDate, ctaLabel),
            "overlay" => RenderPostsGridOverlay(items, showDate, ctaLabel),
            _ => RenderPostsGridDefault(items, columns, showImage, showExcerpt, showDate, ctaLabel)
        };
    }

    private static string RenderPostsGridDefault(List<PublicArticleSummary> items, string columns, bool showImage, bool showExcerpt, bool showDate, string ctaLabel)
    {
        var sb = new StringBuilder($"""<div class="gws-posts-grid gws-posts-grid-cols-{Html(columns)}">""");
        foreach (var article in items)
        {
            sb.Append($"""<a class="gws-posts-grid-item" href="/blog/{Html(article.Slug)}">""");
            if (showImage && !string.IsNullOrWhiteSpace(article.HeroImageUrl))
            {
                sb.Append($"""<img src="{Html(article.HeroImageUrl)}" alt="" class="gws-posts-grid-img" />""");
            }
            sb.Append($"""<div class="gws-posts-grid-body">{PostDate(article, showDate)}<h3 class="gws-posts-grid-title">{Html(article.Title)}</h3>""");
            if (showExcerpt && !string.IsNullOrWhiteSpace(article.MetaDescription))
            {
                sb.Append($"""<p class="gws-posts-grid-excerpt">{Html(article.MetaDescription)}</p>""");
            }
            sb.Append($"""<span class="gws-posts-grid-cta">{Html(ctaLabel)}</span></div></a>""");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // Horizontal thumbnail-left, text-right rows - the "list" layout news/magazine themes
    // typically offer as a denser alternative to a card grid.
    private static string RenderPostsGridList(List<PublicArticleSummary> items, bool showImage, bool showExcerpt, bool showDate, string ctaLabel)
    {
        var sb = new StringBuilder("""<div class="gws-posts-grid-list">""");
        foreach (var article in items)
        {
            sb.Append($"""<a class="gws-posts-grid-list-item" href="/blog/{Html(article.Slug)}">""");
            if (showImage && !string.IsNullOrWhiteSpace(article.HeroImageUrl))
            {
                sb.Append($"""<img src="{Html(article.HeroImageUrl)}" alt="" class="gws-posts-grid-list-img" />""");
            }
            sb.Append($"""<div class="gws-posts-grid-body">{PostDate(article, showDate)}<h3 class="gws-posts-grid-title">{Html(article.Title)}</h3>""");
            if (showExcerpt && !string.IsNullOrWhiteSpace(article.MetaDescription))
            {
                sb.Append($"""<p class="gws-posts-grid-excerpt">{Html(article.MetaDescription)}</p>""");
            }
            sb.Append($"""<span class="gws-posts-grid-cta">{Html(ctaLabel)}</span></div></a>""");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // Single-column, large top image per post - a traditional blog-list template, denser
    // presentation than the default grid's cards but richer than the "list" layout's small thumb.
    private static string RenderPostsGridClassic(List<PublicArticleSummary> items, bool showImage, bool showExcerpt, bool showDate, string ctaLabel)
    {
        var sb = new StringBuilder("""<div class="gws-posts-grid-classic">""");
        foreach (var article in items)
        {
            sb.Append($"""<a class="gws-posts-grid-classic-item" href="/blog/{Html(article.Slug)}">""");
            if (showImage && !string.IsNullOrWhiteSpace(article.HeroImageUrl))
            {
                sb.Append($"""<img src="{Html(article.HeroImageUrl)}" alt="" class="gws-posts-grid-classic-img" />""");
            }
            sb.Append($"""<div class="gws-posts-grid-body">{PostDate(article, showDate)}<h3 class="gws-posts-grid-title">{Html(article.Title)}</h3>""");
            if (showExcerpt && !string.IsNullOrWhiteSpace(article.MetaDescription))
            {
                sb.Append($"""<p class="gws-posts-grid-excerpt">{Html(article.MetaDescription)}</p>""");
            }
            sb.Append($"""<span class="gws-posts-grid-cta">{Html(ctaLabel)}</span></div></a>""");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // Image with the title/date/CTA overlaid directly on it via an absolute gradient scrim -
    // ignores showImage/showExcerpt (the whole point of this layout is the image), and an
    // article with no HeroImageUrl falls back to a plain dark card rather than an empty box.
    private static string RenderPostsGridOverlay(List<PublicArticleSummary> items, bool showDate, string ctaLabel)
    {
        var sb = new StringBuilder("""<div class="gws-posts-grid gws-posts-grid-overlay">""");
        foreach (var article in items)
        {
            var hasImage = !string.IsNullOrWhiteSpace(article.HeroImageUrl);
            sb.Append($"""<a class="gws-posts-grid-overlay-item{(hasImage ? "" : " gws-posts-grid-overlay-noimage")}" href="/blog/{Html(article.Slug)}">""");
            if (hasImage)
            {
                sb.Append($"""<img src="{Html(article.HeroImageUrl!)}" alt="" class="gws-posts-grid-overlay-img" />""");
            }
            sb.Append($"""<div class="gws-posts-grid-overlay-scrim"><div class="gws-posts-grid-body">{PostDate(article, showDate)}<h3 class="gws-posts-grid-title">{Html(article.Title)}</h3><span class="gws-posts-grid-cta">{Html(ctaLabel)}</span></div></div></a>""");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string PostDate(PublicArticleSummary article, bool showDate) =>
        showDate && article.PublishedAt.HasValue
            ? $"""<span class="gws-posts-grid-date">{Html(article.PublishedAt.Value.ToString("MMMM d, yyyy"))}</span>"""
            : "";

    // Recommendations for a SPECIFIC, admin-picked anchor article (sourceArticleSlug), not the
    // current page - there's no ambient "current article" concept outside the hardcoded
    // /blog/{slug} route, so this widget is reusable anywhere (e.g. a landing page built around a
    // cornerstone piece). relatedPostsByAnchorSlug is precomputed by the caller (Program.cs) via
    // IRelatedArticlesService, once per distinct anchor slug on the whole page - this function
    // itself does no DB access, matching this renderer's own "zero DB dependency" contract.
    private static string RenderRelatedPosts(IReadOnlyDictionary<string, string> p, IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>> relatedPostsByAnchorSlug, bool editMode)
    {
        var sourceSlug = Get(p, "sourceArticleSlug");
        if (string.IsNullOrWhiteSpace(sourceSlug))
        {
            return editMode
                ? """<div class="gws-related-posts-empty">Pick a source article in the Inspector.</div>"""
                : """<div class="gws-related-posts-empty">No related posts yet.</div>""";
        }

        if (!relatedPostsByAnchorSlug.TryGetValue(sourceSlug, out var related) || related.Count == 0)
        {
            return """<div class="gws-related-posts-empty">No related posts yet.</div>""";
        }

        var count = Math.Clamp(GetInt(p, "count", 3), 1, 6);
        var showImage = Get(p, "showImage", "true") == "true";

        var sb = new StringBuilder("""<div class="gws-related-posts">""");
        foreach (var article in related.Take(count))
        {
            sb.Append($"""<a class="gws-related-posts-item" href="/blog/{Html(article.Slug)}">""");
            if (showImage && !string.IsNullOrWhiteSpace(article.HeroImageUrl))
            {
                sb.Append($"""<img src="{Html(article.HeroImageUrl!)}" alt="" class="gws-related-posts-img" />""");
            }
            sb.Append($"""<span class="gws-related-posts-title">{Html(article.Title)}</span></a>""");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // <details>/<summary> gives collapsible behavior natively, no JS needed — matches this
    // codebase's preference for the simplest mechanism that actually works.
    // Workstream C, Tier 1 (FAQ + schema markup) - "isFaq" is an admin opt-in, off by default,
    // since an accordion is also used for plenty of genuinely non-FAQ collapsible content (a
    // spec sheet, a changelog) that would be actively wrong to mark up as Google FAQPage data.
    private static string RenderAccordion(IReadOnlyDictionary<string, string> p, bool editMode = false)
    {
        var itemsJson = Get(p, "itemsJson");
        var isFaq = Get(p, "isFaq") == "true";
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder("""<div class="gws-accordion">""");
            var faqEntries = new List<FaqQuestionSchema>();
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var question = item["question"]?.GetValue<string>() ?? string.Empty;
                var answer = item["answer"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(question)) continue;

                sb.Append($"""
                    <details class="gws-accordion-item">
                      <summary class="gws-accordion-question"{InlineEditAttrs(editMode, $"itemsJson[{index}].question")}>{Html(question)}</summary>
                      <div class="gws-accordion-answer"{InlineRichAttrs(editMode, $"itemsJson[{index}].answer", answer)}>{Markdown.ToHtml(answer, MarkdownPipeline)}</div>
                    </details>
                    """);

                if (isFaq && !string.IsNullOrWhiteSpace(answer))
                {
                    faqEntries.Add(new FaqQuestionSchema("Question", question, new FaqAnswerSchema("Answer", Markdown.ToHtml(answer, MarkdownPipeline))));
                }
            }
            sb.Append("</div>");

            // Not emitted in edit mode - it's SEO metadata for the public page, not something
            // an author needs to see reflected in the Studio's own live-preview iframe.
            if (isFaq && !editMode && faqEntries.Count > 0)
            {
                var schema = new FaqPageSchema("https://schema.org", "FAQPage", faqEntries);
                // System.Text.Json's default encoder escapes '<'/'>'/'&' to \uXXXX, so this can
                // never accidentally close the surrounding <script> tag early.
                sb.Append($"""<script type="application/ld+json">{JsonSerializer.Serialize(schema)}</script>""");
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record FaqPageSchema(
        [property: JsonPropertyName("@context")] string Context,
        [property: JsonPropertyName("@type")] string Type,
        [property: JsonPropertyName("mainEntity")] List<FaqQuestionSchema> MainEntity);

    private sealed record FaqQuestionSchema(
        [property: JsonPropertyName("@type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("acceptedAnswer")] FaqAnswerSchema AcceptedAnswer);

    private sealed record FaqAnswerSchema(
        [property: JsonPropertyName("@type")] string Type,
        [property: JsonPropertyName("text")] string Text);

    // Workstream C, Tier 2 (stats/counter block) - static shell only (matching decision 4's
    // "client-side, not server-rendered" precedent from the TOC/reading-progress widgets); the
    // number starts at "0" with its real target in a data attribute, and
    // BuildStatsCounterRuntimeScript animates it up once scrolled into view. In edit mode the
    // real value renders directly instead (no animation, no data attribute) - counting up from
    // zero every time an admin clicks around the Studio would be distracting, not helpful, and
    // the runtime script is never guaranteed to run inside that preview.
    private static string RenderStats(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var itemsJson = Get(p, "itemsJson");
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder("""<div class="gws-stats">""");
            var index = -1;
            foreach (var item in node.OfType<JsonObject>())
            {
                index++;
                var value = item["value"]?.GetValue<string>() ?? string.Empty;
                var suffix = item["suffix"]?.GetValue<string>() ?? string.Empty;
                var label = item["label"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value)) continue;

                var numberAttrs = editMode ? "" : $" data-gws-counter-target=\"{Html(value)}\"";
                var numberText = editMode ? Html(value) : "0";

                sb.Append($"""
                    <div class="gws-stats-item">
                      <div class="gws-stats-value"><span class="gws-stats-number"{numberAttrs}>{numberText}</span><span class="gws-stats-suffix"{InlineEditAttrs(editMode, $"itemsJson[{index}].suffix")}>{Html(suffix)}</span></div>
                      <div class="gws-stats-label"{InlineEditAttrs(editMode, $"itemsJson[{index}].label")}>{Html(label)}</div>
                    </div>
                    """);
            }
            sb.Append("</div>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // Posts to /cms/{siteSlug}/{pageSlug}/submit (see Program.cs), which stores the
    // submission via IFormSubmissionService. The "company" field is a honeypot: hidden
    // from real visitors via CSS, so a filled-in value marks the request as a bot without
    // telling the bot it was caught.
    // Posts to a fixed /cms/{siteSlug}/submit rather than embedding the page path in the URL
    // — a nested page's path (e.g. "services/web-dev") can't appear before a fixed "/submit"
    // segment once the live site's page route becomes a catch-all, so the path travels as a
    // hidden field instead, same as the honeypot.
    // Field *labels* are prose a visitor reads, so they edit in place like any other text. A
    // field's type / required / key / options are configuration with no visitor-facing text and
    // stay in the Inspector - that is the line, not "flat prop vs structured JSON".
    private static string RenderForm(IReadOnlyDictionary<string, string> p, string siteSlug, string pageSlug, bool editMode = false, string? turnstileSiteKey = null)
    {
        var fields = ParseFormFields(Get(p, "fieldsJson"));
        var sb = new StringBuilder();
        var protectedForm = turnstileSiteKey is not null && !editMode;
        var protectionAttribute = protectedForm ? " data-contact-form" : string.Empty;
        sb.Append($"""<form class="gws-form" method="post" action="/cms/{Html(siteSlug)}/submit"{protectionAttribute}>""");
        sb.Append($"""<input type="hidden" name="_path" value="{Html(pageSlug)}" />""");

        foreach (var field in fields)
        {
            sb.Append($"""<label class="gws-form-field"><span class="gws-form-label"{InlineEditAttrs(editMode, $"fieldsJson[{field.SourceIndex}].label")}>""");
            sb.Append(Html(field.Label));
            if (field.Required) sb.Append("""<span class="gws-form-required">*</span>""");
            sb.Append("</span>");
            sb.Append(RenderFormControl(field));
            sb.Append("</label>");
        }

        // Leading underscore, matching "_path" above - UpdateFormField (CmsBuilderEditor.razor)
        // maps every non-alphanumeric character in an admin-typed label to '-', so a derived
        // field key can never contain '_' and can never collide with this name. It previously
        // used "company", which collided with the "Company" FormFieldRole's own derived key and
        // silently dropped every real submission through a field with that exact label.
        sb.Append("""<input type="text" name="_hp" class="gws-form-honeypot" tabindex="-1" autocomplete="off" />""");
        if (protectedForm)
        {
            if (!string.IsNullOrWhiteSpace(turnstileSiteKey))
                sb.Append($"""<div class="gws-contact-verification" data-sitekey="{Html(turnstileSiteKey)}"></div>""");
            var message = string.IsNullOrWhiteSpace(turnstileSiteKey)
                ? "The contact form is temporarily unavailable. Please try again shortly."
                : "Complete the verification to send your message.";
            sb.Append($"""<p class="gws-form-status" role="status" aria-live="polite">{message}</p><noscript><p>Please enable JavaScript to verify and send your message.</p></noscript>""");
        }
        var disabled = protectedForm ? " disabled" : string.Empty;
        sb.Append($"""<button type="submit" class="btn btn-primary gws-form-submit"{disabled}{InlineEditAttrs(editMode, "submitLabel")}>{Html(Get(p, "submitLabel", "Submit"))}</button>""");
        sb.Append("</form>");
        return sb.ToString();
    }

    private static string RenderFormControl(FormFieldDefinition field)
    {
        var required = field.Required ? " required" : string.Empty;
        var name = Html(field.Key);
        return field.Type switch
        {
            "textarea" => $"""<textarea name="{name}" rows="4"{required}></textarea>""",
            "select" => $"""<select name="{name}"{required}><option value="">Select…</option>{SelectOptions(field.OptionsJson)}</select>""",
            "checkbox" => $"""<input type="checkbox" name="{name}"{required} />""",
            "tel" => $"""<input type="tel" name="{name}"{required} />""",
            "email" => $"""<input type="email" name="{name}"{required} />""",
            _ => $"""<input type="text" name="{name}"{required} />"""
        };
    }

    private static string SelectOptions(string optionsJson)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(optionsJson) ? "[]" : optionsJson) as JsonArray;
            if (node is null) return string.Empty;
            return string.Concat(node
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(opt => $"""<option value="{Html(opt)}">{Html(opt)}</option>"""));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<FormFieldDefinition> ParseFormFields(string fieldsJson)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(fieldsJson) ? "[]" : fieldsJson) as JsonArray;
            if (node is null) return [];

            // SourceIndex is the field's position in the stored array, captured BEFORE the
            // keyless-field filter below. Inline label editing addresses fields as
            // "fieldsJson[n].label", so numbering the rendered fields instead would write to the
            // wrong element on any form that contains a keyless field.
            return node.OfType<JsonObject>().Select((obj, sourceIndex) => new FormFieldDefinition(
                Key: obj["key"]?.GetValue<string>() ?? string.Empty,
                Label: obj["label"]?.GetValue<string>() ?? string.Empty,
                Type: obj["type"]?.GetValue<string>() ?? "text",
                Required: obj["required"]?.GetValue<bool>() ?? false,
                OptionsJson: obj["optionsJson"]?.GetValue<string>() ?? string.Empty,
                SourceIndex: sourceIndex
            )).Where(f => !string.IsNullOrWhiteSpace(f.Key)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed record FormFieldDefinition(string Key, string Label, string Type, bool Required, string OptionsJson, int SourceIndex = 0);

    private static string HeroCta(string label, string href, string cssClass, bool editMode, string inlinePropKey) =>
        string.IsNullOrWhiteSpace(label)
            ? string.Empty
            : $"""<a href="{Html(HrefOrHash(href))}" class="btn {cssClass}"{InlineEditAttrs(editMode, inlinePropKey)}>{Html(label)}</a>""";

    // Workstream C, Tier 2 (hero variants) - "layout" picks one of 3 presentations over the
    // identical headline/subline/CTA content, same style-variant-as-a-prop convention as
    // posts-grid's own "layout" Prop. Falls back to the plain "default" markup whenever the
    // variant-specific media Prop it needs isn't set yet, so a half-configured hero never
    // renders a broken/empty background.
    private static string RenderHero(IReadOnlyDictionary<string, string> p, bool editMode)
    {
        var content = $"""
            <h1 class="gws-hero-headline"{InlineEditAttrs(editMode, "headline")}>{Html(Get(p, "headline"))}</h1>
            {(HasValue(p, "subline") ? $"""<div class="gws-hero-subline"{InlineRichAttrs(editMode, "subline", Get(p, "subline"))}>{Markdown.ToHtml(Get(p, "subline"), MarkdownPipeline)}</div>""" : "")}
            <div class="gws-hero-actions">
              {HeroCta(Get(p, "cta1Label"), Get(p, "cta1Href"), "btn-primary", editMode, "cta1Label")}
              {HeroCta(Get(p, "cta2Label"), Get(p, "cta2Href"), "btn-ghost", editMode, "cta2Label")}
            </div>
            """;

        if (Get(p, "layout") == "video-background" && HasValue(p, "backgroundVideoUrl"))
        {
            var overlay = Math.Clamp(GetInt(p, "overlayOpacity", 40), 0, 100);
            var posterAttr = HasValue(p, "posterImageUrl") ? $" poster=\"{Html(Get(p, "posterImageUrl"))}\"" : "";
            return $"""
                <div class="gws-hero gws-hero-video gws-align-{Html(Align(p))}">
                  <video class="gws-hero-video-bg" autoplay muted loop playsinline{posterAttr}>
                    <source src="{Html(Get(p, "backgroundVideoUrl"))}" />
                  </video>
                  <div class="gws-hero-video-overlay" style="opacity:{overlay}%"></div>
                  <div class="gws-hero-video-content">{content}</div>
                </div>
                """;
        }

        if (Get(p, "layout") == "split" && HasValue(p, "splitImageUrl"))
        {
            var imagePosition = Get(p, "splitImagePosition", "right") == "left" ? "left" : "right";
            var imageEl = $"""<img class="gws-hero-split-img" src="{Html(Get(p, "splitImageUrl"))}" alt="" />""";
            var textEl = $"""<div class="gws-hero-split-text">{content}</div>""";
            return $"""
                <div class="gws-hero gws-hero-split gws-hero-split-{Html(imagePosition)} gws-align-{Html(Align(p))}">
                  {(imagePosition == "left" ? imageEl + textEl : textEl + imageEl)}
                </div>
                """;
        }

        return $"""
            <div class="gws-hero gws-align-{Html(Align(p))}">
              {content}
            </div>
            """;
    }

    // Emitted only in edit mode - lets the click-to-select script's contenteditable
    // affordance target the right widget prop when the user types directly on canvas.
    // Focus/cursor placement for a contenteditable element happens on mousedown, before
    // the edit-mode script's capture-phase click listener runs its e.preventDefault() -
    // so preventDefault (needed to stop a real <a>/<form>'s own default action) never
    // interferes with the native "click to place cursor and type" behavior here.
    // A section's widgets fill it edge to edge, so in practice there was nowhere to click the
    // section itself - a click almost always landed on a widget and selecting a section was
    // effectively impossible. This gives every section an explicit, always-present target in edit
    // mode (the label chip both WordPress and Squarespace put on a section), instead of asking
    // people to find a few pixels of padding.
    private static string SectionHandle(LayoutSection section, bool editMode) =>
        editMode
            ? $"""<button type="button" class="gws-section-handle" data-gws-section-handle="{Html(section.Id)}">{Html(string.IsNullOrWhiteSpace(section.Label) ? "Section" : section.Label)}</button>"""
            : string.Empty;

    // Markdown constructs the canvas editor's HTML->Markdown serializer (professionalEditor.js's
    // serialize()) cannot represent. A prop containing any of these stays click-to-select and is
    // edited in the Inspector instead, so inline editing can never silently flatten a table or
    // drop a footnote. scripts/scan-cms-markdown.py reports the same set across a database - it
    // found zero occurrences in production, so this is a guard against future content rather
    // than a workaround for existing content.
    private static readonly Regex[] UnserializableMarkdown =
    [
        new(@"^\s*\|.*\|\s*$", RegexOptions.Multiline),
        new(@"\[\^[^\]]+\]"),
        new(@"^\s*[-*]\s+\[[ xX]\]", RegexOptions.Multiline),
        new(@"\$\$|\\\(|\\\["),
        new(@"!\[[^\]]*\]\("),
        new(@"^#{4,}\s", RegexOptions.Multiline),
        new(@"^:\s{1,3}\S", RegexOptions.Multiline),
        new(@"^:::", RegexOptions.Multiline),
        new(@"^\*\[[^\]]+\]:", RegexOptions.Multiline),
        new(@"^\[[^\]]+\]:\s*\S", RegexOptions.Multiline),
    ];

    internal static bool CanEditInline(string markdown) =>
        !string.IsNullOrEmpty(markdown) && !UnserializableMarkdown.Any(rx => rx.IsMatch(markdown));

    // Rich (Markdown-backed) inline editing. contenteditable is "true" rather than
    // "plaintext-only" so bold/italic/link survive typing; the canvas posts innerHTML up and the
    // parent converts it back to Markdown. Empty content is editable too - there is nothing to
    // lose - which is what lets an author fill in a blank paragraph in place.
    private static string InlineRichAttrs(bool editMode, string propKey, string markdown) =>
        editMode && (string.IsNullOrEmpty(markdown) || CanEditInline(markdown))
            ? $" contenteditable=\"true\" data-gws-inline-prop=\"{propKey}\" data-gws-inline-rich=\"1\""
            : string.Empty;

    private static string InlineEditAttrs(bool editMode, string propKey) =>
        editMode ? $" contenteditable=\"plaintext-only\" data-gws-inline-prop=\"{propKey}\"" : "";

    private static readonly string[] AllowedHrefSchemes = ["http", "https", "mailto", "tel"];

    // Html() only HTML-encodes for the attribute context - it does not neutralize a dangerous
    // URI scheme, since none of javascript:/data:/vbscript: contain characters that need
    // encoding. A widget's href/link fields are editable by any Contributor (and, since the
    // Developer API's cms-pages:write scope, by a fully machine-driven credential with no human
    // ever looking at the editor), so an unvalidated scheme here would let that role run
    // arbitrary script for every site visitor who clicks the link. Relative paths/anchors/query
    // strings have no scheme to check and are passed through; an absolute URL's scheme must be
    // on the allowlist. Browsers strip tab/newline/carriage-return before scheme-sniffing a URL
    // (a known way to sneak "java\tscript:" past a naive check) - stripped here first so this
    // check sees the same string a browser would act on.
    private static string HrefOrHash(string href)
    {
        var value = (href ?? string.Empty).Replace("\t", "").Replace("\n", "").Replace("\r", "").Trim();
        if (value.Length == 0) return "#";
        if (value[0] is '/' or '#' or '?') return value;
        if (Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri))
        {
            if (!uri.IsAbsoluteUri) return value;
            if (AllowedHrefSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) return value;
        }
        return "#";
    }

    private static string OpenInNewTabAttrs(IReadOnlyDictionary<string, string> p) =>
        Get(p, "openInNewTab") == "true" ? " target=\"_blank\" rel=\"noopener noreferrer\"" : string.Empty;

    private static string Align(IReadOnlyDictionary<string, string> p) => Get(p, "align", "left");

    private static string Tag(IReadOnlyDictionary<string, string> p)
    {
        var level = Get(p, "level", "h2");
        return level is "h1" or "h2" or "h3" or "h4" ? level : "h2";
    }

    private static string BgClass(string background) => background switch
    {
        "light" => "gws-bg-light",
        "dark" => "gws-bg-dark",
        "accent" => "gws-bg-accent",
        _ => string.Empty
    };

    private static string PadClass(string padding) => padding switch
    {
        "none" => "gws-pad-none",
        "sm" => "gws-pad-sm",
        "lg" => "gws-pad-lg",
        "xl" => "gws-pad-xl",
        _ => "gws-pad-md"
    };

    private static string ColsClass(string columnLayout) => columnLayout switch
    {
        "half-half" => "gws-columns gws-cols-2",
        "one-third-two-thirds" => "gws-columns gws-cols-1-2",
        "two-thirds-one-third" => "gws-columns gws-cols-2-1",
        "thirds" => "gws-columns gws-cols-3",
        _ => "gws-columns gws-cols-1"
    };

    private static bool HasValue(IReadOnlyDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v);

    private static string Get(IReadOnlyDictionary<string, string> p, string key, string fallback = "") =>
        p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> p, string key, int fallback) =>
        p.TryGetValue(key, out var v) && int.TryParse(v, out var result) ? result : fallback;

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
