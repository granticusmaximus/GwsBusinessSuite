using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
    public static string Render(string blocksJson, string siteSlug = "", string pageSlug = "", bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null, bool isLoggedIn = false, DesignTokenSet? tokens = null)
        => Render(CmsBuilderJson.ParseLayout(blocksJson), siteSlug, pageSlug, editMode, articles, isLoggedIn, tokens);

    public static string Render(PageLayout? layout, string siteSlug = "", string pageSlug = "", bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null, bool isLoggedIn = false, DesignTokenSet? tokens = null)
    {
        if (layout is null || layout.Sections.Count == 0)
        {
            return editMode
                ? """<div class="gws-canvas-empty" data-gws-empty-canvas="1">Drop widgets here to start building this page.</div>"""
                : string.Empty;
        }

        var effectiveArticles = articles ?? NoArticles;
        var html = new StringBuilder();
        foreach (var section in layout.Sections)
        {
            html.Append(RenderSection(section, siteSlug, pageSlug, editMode, effectiveArticles, isLoggedIn, tokens));
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
            width: 22px; height: 22px; border-radius: 6px;
            background: #2563eb; color: #fff;
            display: flex; align-items: center; justify-content: center;
            font-size: 13px; line-height: 1; cursor: grab;
            opacity: 0; transition: opacity 0.1s ease;
          }
          .gws-editable:hover .gws-drag-handle { opacity: 1; }
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

    private static string RenderSection(LayoutSection section, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles, bool isLoggedIn, DesignTokenSet? tokens = null)
    {
        var sectionClass = $"gws-section {BgClass(section.Background)} {PadClass(section.Padding)}".TrimEnd();
        var sectionAttrs = editMode ? $" data-gws-section-id=\"{Html(section.Id)}\"" : "";

        if (section.LayoutMode == CmsSectionLayoutModes.Freeform)
        {
            return RenderFreeformSection(section, sectionClass, sectionAttrs, siteSlug, pageSlug, editMode, articles, isLoggedIn, tokens);
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
                var inner = WrapWithStyle(RenderWidget(widget, siteSlug, pageSlug, editMode, articles), widget.Style, tokens);
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
                // Wrapped OUTSIDE WrapWithStyle so a widget's own background/padding
                // overrides can never clip the selection outline, and closest('[data-gws-
                // widget-id]') in the edit-mode script always resolves reliably regardless
                // of per-widget style config.
                sb.Append(editMode
                    ? $"""<div class="gws-editable" data-gws-widget-id="{Html(widget.Id)}" data-gws-widget-type="{Html(widget.WidgetType)}">{hiddenHint}<div class="gws-drag-handle" data-gws-drag-handle-for="{Html(widget.Id)}">&#10247;</div>{inner}</div>"""
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
    private static string RenderFreeformSection(LayoutSection section, string sectionClass, string sectionAttrs, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles, bool isLoggedIn, DesignTokenSet? tokens)
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
            var inner = WrapWithStyle(RenderWidget(widget, siteSlug, pageSlug, editMode, articles), widget.Style, tokens);
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

    // Wraps a widget's rendered HTML in a styled container when it has any per-widget
    // style override set (Phase 6) — otherwise returns the inner HTML untouched, so
    // widgets with no overrides render byte-for-byte as they did before this feature.
    private static string WrapWithStyle(string innerHtml, WidgetStyle style, DesignTokenSet? tokens = null)
    {
        var inlineStyle = style.ToInlineStyle(tokens);
        return inlineStyle.Length == 0
            ? innerHtml
            : $"""<div class="gws-widget-style" style="{Html(inlineStyle)}">{innerHtml}</div>""";
    }

    // Phase 5 (Native No-Code Interactions & Animation Engine) — wraps a widget's rendered
    // HTML in a data-gws-interaction container the shared runtime script
    // (BuildInteractionRuntimeScript) reads at load time. Null Interaction (the default)
    // returns the inner HTML untouched, same "opt-in wrapper" contract as WrapWithStyle above.
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

    // Inline <script>, matching BuildEditModeScript's pattern - injected once per public page
    // response (see Program.cs's public page route and the static-export route) rather than
    // served as a wwwroot/js file, so the static-export zip stays fully self-contained with no
    // extra file to bundle. A no-op when the page has no data-gws-interaction elements at all.
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

    private static string RenderWidget(LayoutWidget widget, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles)
    {
        var p = widget.Props;
        return widget.WidgetType switch
        {
            "hero" => $"""
                <div class="gws-hero gws-align-{Html(Align(p))}">
                  <h1 class="gws-hero-headline"{InlineEditAttrs(editMode, "headline")}>{Html(Get(p, "headline"))}</h1>
                  {(HasValue(p, "subline") ? $"""<div class="gws-hero-subline"{InlineRichAttrs(editMode, "subline", Get(p, "subline"))}>{Markdown.ToHtml(Get(p, "subline"), MarkdownPipeline)}</div>""" : "")}
                  <div class="gws-hero-actions">
                    {HeroCta(Get(p, "cta1Label"), Get(p, "cta1Href"), "btn-primary", editMode, "cta1Label")}
                    {HeroCta(Get(p, "cta2Label"), Get(p, "cta2Href"), "btn-ghost", editMode, "cta2Label")}
                  </div>
                </div>
                """,
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
            "accordion" => RenderAccordion(Get(p, "itemsJson"), editMode),
            "spacer" => $"""<div class="gws-spacer" style="height:{GetInt(p, "height", 48)}px"></div>""",
            "divider" => $"""<hr class="gws-divider gws-divider-{Html(Get(p, "style", "solid"))}" />""",
            "html" => Get(p, "content"),
            "form" => RenderForm(p, siteSlug, pageSlug, editMode),
            "posts-grid" => RenderPostsGrid(p, articles),
            _ => string.Empty
        };
    }

    // WordPress "loop"-equivalent: a live grid of the most recently published Articles,
    // not a static block - articles is whatever the caller (Program.cs) fetched for this
    // request, already filtered to publicly-visible ones and ordered newest-first.
    private static string RenderPostsGrid(IReadOnlyDictionary<string, string> p, IReadOnlyList<PublicArticleSummary> articles)
    {
        var count = Math.Clamp(GetInt(p, "count", 3), 1, 12);
        var columns = Get(p, "columns", "3");
        var showImage = Get(p, "showImage", "true") == "true";
        var showExcerpt = Get(p, "showExcerpt", "true") == "true";
        var ctaLabel = Get(p, "ctaLabel", "Read More");

        var items = articles.Take(count).ToList();
        if (items.Count == 0)
        {
            return """<div class="gws-posts-grid-empty">No published posts yet.</div>""";
        }

        var sb = new StringBuilder($"""<div class="gws-posts-grid gws-posts-grid-cols-{Html(columns)}">""");
        foreach (var article in items)
        {
            sb.Append($"""<a class="gws-posts-grid-item" href="/blog/{Html(article.Slug)}">""");
            if (showImage && !string.IsNullOrWhiteSpace(article.HeroImageUrl))
            {
                sb.Append($"""<img src="{Html(article.HeroImageUrl)}" alt="" class="gws-posts-grid-img" />""");
            }
            sb.Append($"""<div class="gws-posts-grid-body"><h3 class="gws-posts-grid-title">{Html(article.Title)}</h3>""");
            if (showExcerpt && !string.IsNullOrWhiteSpace(article.MetaDescription))
            {
                sb.Append($"""<p class="gws-posts-grid-excerpt">{Html(article.MetaDescription)}</p>""");
            }
            sb.Append($"""<span class="gws-posts-grid-cta">{Html(ctaLabel)}</span></div></a>""");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // <details>/<summary> gives collapsible behavior natively, no JS needed — matches this
    // codebase's preference for the simplest mechanism that actually works.
    private static string RenderAccordion(string itemsJson, bool editMode = false)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder("""<div class="gws-accordion">""");
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
    private static string RenderForm(IReadOnlyDictionary<string, string> p, string siteSlug, string pageSlug, bool editMode = false)
    {
        var fields = ParseFormFields(Get(p, "fieldsJson"));
        var sb = new StringBuilder();
        sb.Append($"""<form class="gws-form" method="post" action="/cms/{Html(siteSlug)}/submit">""");
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
        sb.Append($"""<button type="submit" class="btn btn-primary gws-form-submit"{InlineEditAttrs(editMode, "submitLabel")}>{Html(Get(p, "submitLabel", "Submit"))}</button>""");
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
