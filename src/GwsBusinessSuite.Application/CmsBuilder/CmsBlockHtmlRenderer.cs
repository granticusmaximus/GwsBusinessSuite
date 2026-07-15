using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
        .Build();

    private static readonly IReadOnlyList<PublicArticleSummary> NoArticles = [];

    public static string Render(string blocksJson, string siteSlug = "", string pageSlug = "", bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null)
        => Render(CmsBuilderJson.ParseLayout(blocksJson), siteSlug, pageSlug, editMode, articles);

    public static string Render(PageLayout? layout, string siteSlug = "", string pageSlug = "", bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null)
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
            html.Append(RenderSection(section, siteSlug, pageSlug, editMode, effectiveArticles));
        }

        return html.ToString();
    }

    // Emitted only when editMode is true (see Program.cs's /cms/{siteSlug}/{**pageSlug}
    // gating - never reaches a real visitor). Lets Canvas Studio's live-preview iframe
    // report clicks back to the parent page via postMessage instead of navigating away,
    // and highlights the currently-selected element. See cms-builder-bridge.js for the
    // parent-side half of this bridge.
    public static string BuildEditModeScript() => """
        <style>
          .gws-editable { position: relative; }
          .gws-editable:hover { outline: 1px dashed rgba(37, 99, 235, 0.45); outline-offset: -1px; cursor: pointer; }
          .gws-editor-selected { outline: 2px solid #2563eb !important; outline-offset: -2px; }
          [data-gws-section-id]:hover { outline: 1px dashed rgba(148, 163, 184, 0.5); outline-offset: -1px; }
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
        </style>
        <script>
        (function () {
          var ORIGIN = window.location.origin;
          function send(msg) { window.parent.postMessage(msg, ORIGIN); }

          var drag = null;
          var paletteDragTarget = null;
          var html5Indicator = null;

          function createIndicator() {
            var indicator = document.createElement('div');
            indicator.style.cssText = 'position:fixed;left:0;top:0;width:0;height:3px;background:#2563eb;z-index:100000;pointer-events:none;display:none;border-radius:2px;box-shadow:0 0 0 1px rgba(255,255,255,0.8);';
            document.body.appendChild(indicator);
            return indicator;
          }

          function clearIndicator(indicator) {
            if (indicator) indicator.style.display = 'none';
          }

          function clearDropHighlights() {
            document.querySelectorAll('.is-drop-target').forEach(function (el) {
              el.classList.remove('is-drop-target');
            });
          }

          function clearDropVisuals(indicator) {
            clearIndicator(indicator);
            clearDropHighlights();
          }

          function getSectionId(el) {
            var sectionEl = el && el.closest ? el.closest('[data-gws-section-id]') : null;
            return sectionEl ? sectionEl.getAttribute('data-gws-section-id') : '';
          }

          function getColumnId(el) {
            var columnEl = el && el.closest ? el.closest('[data-gws-column-id]') : null;
            return columnEl ? columnEl.getAttribute('data-gws-column-id') : '';
          }

          function hasType(dataTransfer, type) {
            return !!(dataTransfer && dataTransfer.types && Array.prototype.indexOf.call(dataTransfer.types, type) >= 0);
          }

          function hasExternalDragType(dataTransfer) {
            return hasType(dataTransfer, 'application/x-gws-widget-type')
              || hasType(dataTransfer, 'application/x-gws-global-block-id');
          }

          function resolveDropTarget(clientX, clientY, draggedWidgetId) {
            var el = document.elementFromPoint(clientX, clientY);
            if (!el || !el.closest) return null;

            var emptyCanvas = el.closest('[data-gws-empty-canvas]');
            if (emptyCanvas) {
              return { mode: 'empty', emptyCanvas: emptyCanvas };
            }

            var widgetEl = el.closest('[data-gws-widget-id]');
            if (widgetEl) {
              var widgetId = widgetEl.getAttribute('data-gws-widget-id');
              if (draggedWidgetId && widgetId === draggedWidgetId) return null;
              var rect = widgetEl.getBoundingClientRect();
              return {
                mode: 'widget',
                widgetId: widgetId,
                sectionId: getSectionId(widgetEl),
                columnId: getColumnId(widgetEl),
                columnEl: widgetEl.closest('[data-gws-column-id]'),
                rect: rect,
                insertAfter: clientY > rect.top + rect.height / 2
              };
            }

            var columnEl = el.closest('[data-gws-column-id]');
            if (columnEl) {
              return {
                mode: 'column',
                sectionId: getSectionId(columnEl),
                columnId: getColumnId(columnEl),
                columnEl: columnEl,
                rect: columnEl.getBoundingClientRect()
              };
            }

            return null;
          }

          function drawDropIndicator(target, indicator) {
            clearDropVisuals(indicator);
            if (!target) return;

            if (target.mode === 'empty') {
              target.emptyCanvas.classList.add('is-drop-target');
              return;
            }

            if (target.mode === 'column' && target.columnEl) {
              target.columnEl.classList.add('is-drop-target');
            }

            if (!indicator || !target.rect) return;

            indicator.style.display = 'block';
            indicator.style.left = target.rect.left + 'px';
            indicator.style.width = target.rect.width + 'px';
            indicator.style.top = (target.mode === 'widget' && !target.insertAfter ? target.rect.top : target.rect.bottom) + 'px';
          }

          document.addEventListener('mousedown', function (e) {
            var handle = e.target.closest('[data-gws-drag-handle-for]');
            if (!handle) return;
            e.preventDefault();
            document.body.style.userSelect = 'none';
            drag = {
              widgetId: handle.getAttribute('data-gws-drag-handle-for'),
              indicator: createIndicator(),
              target: null,
              raf: null,
              pendingEvent: null
            };
          });

          function processDragMove() {
            if (!drag) return;
            drag.raf = null;
            var e = drag.pendingEvent;
            if (!e) return;
            drag.target = resolveDropTarget(e.clientX, e.clientY, drag.widgetId);
            drawDropIndicator(drag.target, drag.indicator);
          }

          document.addEventListener('mousemove', function (e) {
            if (!drag) return;
            drag.pendingEvent = e;
            if (drag.raf) return;
            drag.raf = requestAnimationFrame(processDragMove);
          });

          document.addEventListener('mouseup', function () {
            if (!drag) return;
            var draggedId = drag.widgetId;
            var target = drag.target;
            if (drag.raf) cancelAnimationFrame(drag.raf);
            clearDropVisuals(drag.indicator);
            drag.indicator.remove();
            document.body.style.userSelect = '';
            drag = null;

            if (target && target.mode !== 'empty') {
              send({
                type: 'cms:drop',
                widgetId: draggedId,
                sectionId: target.sectionId || '',
                columnId: target.columnId || '',
                targetWidgetId: target.widgetId || '',
                insertAfter: !!target.insertAfter
              });
            }
          });

          document.addEventListener('dragover', function (e) {
            if (!hasExternalDragType(e.dataTransfer)) return;
            var target = resolveDropTarget(e.clientX, e.clientY, null);
            if (!target) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
            if (!html5Indicator) html5Indicator = createIndicator();
            paletteDragTarget = target;
            drawDropIndicator(target, html5Indicator);
          }, true);

          document.addEventListener('drop', function (e) {
            if (!hasExternalDragType(e.dataTransfer)) return;
            e.preventDefault();
            var widgetType = e.dataTransfer.getData('application/x-gws-widget-type') || e.dataTransfer.getData('text/plain');
            var globalBlockId = e.dataTransfer.getData('application/x-gws-global-block-id');
            var target = resolveDropTarget(e.clientX, e.clientY, null) || paletteDragTarget;
            clearDropVisuals(html5Indicator);
            paletteDragTarget = null;
            if (globalBlockId) {
              send({
                type: 'cms:insert-global',
                globalBlockId: globalBlockId,
                sectionId: target && target.sectionId ? target.sectionId : '',
                columnId: target && target.columnId ? target.columnId : '',
                targetWidgetId: target && target.widgetId ? target.widgetId : '',
                insertAfter: !!(target && target.insertAfter)
              });
              return;
            }
            if (!widgetType) return;
            send({
              type: 'cms:insert-widget',
              widgetType: widgetType,
              sectionId: target && target.sectionId ? target.sectionId : '',
              columnId: target && target.columnId ? target.columnId : '',
              targetWidgetId: target && target.widgetId ? target.widgetId : '',
              insertAfter: !!(target && target.insertAfter)
            });
          }, true);

          function findDirectStyleWrapper(container) {
            return Array.prototype.find.call(container.children, function (child) {
              return child.classList && child.classList.contains('gws-widget-style');
            }) || null;
          }

          function applyWidgetStyle(widgetId, inlineStyle, hasAnyOverride) {
            var container = document.querySelector('[data-gws-widget-id="' + widgetId + '"]');
            if (!container) return;

            var wrapper = findDirectStyleWrapper(container);
            if (hasAnyOverride) {
              if (!wrapper) {
                wrapper = document.createElement('div');
                wrapper.className = 'gws-widget-style';
                Array.prototype.slice.call(container.childNodes).forEach(function (node) {
                  if (node.nodeType === 1 && node.hasAttribute && node.hasAttribute('data-gws-drag-handle-for')) return;
                  wrapper.appendChild(node);
                });
                container.appendChild(wrapper);
              }
              wrapper.setAttribute('style', inlineStyle || '');
              return;
            }

            if (wrapper) {
              while (wrapper.firstChild) {
                container.insertBefore(wrapper.firstChild, wrapper);
              }
              wrapper.remove();
            }
          }

          function applySectionClass(sectionId, cssClass) {
            var section = document.querySelector('[data-gws-section-id="' + sectionId + '"]');
            if (section) {
              section.className = cssClass || 'gws-section';
            }
          }

          function highlight(widgetId) {
            var prev = document.querySelector('.gws-editor-selected');
            if (prev) prev.classList.remove('gws-editor-selected');
            if (widgetId) {
              var el = document.querySelector('[data-gws-widget-id="' + widgetId + '"]');
              if (el) el.classList.add('gws-editor-selected');
            }
          }

          document.addEventListener('click', function (e) {
            var widgetEl = e.target.closest('[data-gws-widget-id]');
            var sectionEl = e.target.closest('[data-gws-section-id]');
            if (widgetEl) {
              // Focus/cursor placement for a contenteditable target already happened on
              // mousedown, before this capture-phase click listener runs - preventDefault
              // here only stops a real <a>/<form>'s own default action, it can't undo the
              // focus that's already landed.
              e.preventDefault();
              highlight(widgetEl.getAttribute('data-gws-widget-id'));
              send({ type: 'cms:select', sectionId: sectionEl ? sectionEl.getAttribute('data-gws-section-id') : '', widgetId: widgetEl.getAttribute('data-gws-widget-id') });
            } else if (sectionEl) {
              e.preventDefault();
              highlight(null);
              send({ type: 'cms:select-section', sectionId: sectionEl.getAttribute('data-gws-section-id') });
            }
          }, true);

          // Single-line fields (heading/paragraph/button label/hero headline+CTAs) commit
          // on Enter instead of inserting a line break, matching how a normal text input
          // behaves - blur() below is what actually sends the edit (see the blur listener).
          document.addEventListener('keydown', function (e) {
            if (e.key !== 'Enter') return;
            var el = e.target.closest('[data-gws-inline-prop]');
            if (el) { e.preventDefault(); el.blur(); }
          }, true);

          // blur doesn't bubble, so this must be a capture-phase listener to observe it via
          // delegation rather than one listener per editable element.
          document.addEventListener('blur', function (e) {
            var el = e.target;
            if (!(el instanceof Element) || !el.hasAttribute('data-gws-inline-prop')) return;
            var widgetEl = el.closest('[data-gws-widget-id]');
            var sectionEl = el.closest('[data-gws-section-id]');
            if (!widgetEl) return;
            send({
              type: 'cms:edit',
              sectionId: sectionEl ? sectionEl.getAttribute('data-gws-section-id') : '',
              widgetId: widgetEl.getAttribute('data-gws-widget-id'),
              prop: el.getAttribute('data-gws-inline-prop'),
              value: el.innerText
            });
          }, true);

          window.addEventListener('message', function (e) {
            if (e.origin !== ORIGIN || !e.data || typeof e.data !== 'object') return;
            if (e.data.type === 'cms:sync-selection') {
              highlight(e.data.widgetId || null);
            } else if (e.data.type === 'cms:prop-changed') {
              var el = document.querySelector('[data-gws-widget-id="' + e.data.widgetId + '"] [data-gws-inline-prop="' + e.data.prop + '"], [data-gws-widget-id="' + e.data.widgetId + '"][data-gws-inline-prop="' + e.data.prop + '"]');
              // Don't clobber an in-progress edit - only patch elements the user isn't
              // actively typing in (e.g. the same prop edited from the Inspector instead).
              if (el && document.activeElement !== el) {
                el.innerText = e.data.value;
              }
            } else if (e.data.type === 'cms:style-changed') {
              applyWidgetStyle(e.data.widgetId, e.data.inlineStyle || '', !!e.data.hasAnyOverride);
            } else if (e.data.type === 'cms:section-changed') {
              applySectionClass(e.data.sectionId, e.data.cssClass || '');
            } else if (e.data.type === 'cms:palette-drag-end') {
              clearDropVisuals(html5Indicator);
              paletteDragTarget = null;
            }
          });

          send({ type: 'cms:ready' });
        })();
        </script>
        """;

    private static string RenderSection(LayoutSection section, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles)
    {
        var sectionClass = $"gws-section {BgClass(section.Background)} {PadClass(section.Padding)}".TrimEnd();
        var columnsClass = ColsClass(section.ColumnLayout);
        var sectionAttrs = editMode ? $" data-gws-section-id=\"{Html(section.Id)}\"" : "";

        var sb = new StringBuilder();
        sb.Append($"""<section class="{Html(sectionClass)}"{sectionAttrs}><div class="{Html(columnsClass)}">""");

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
                var inner = WrapWithStyle(RenderWidget(widget, siteSlug, pageSlug, editMode, articles), widget.Style);
                // Wrapped OUTSIDE WrapWithStyle so a widget's own background/padding
                // overrides can never clip the selection outline, and closest('[data-gws-
                // widget-id]') in the edit-mode script always resolves reliably regardless
                // of per-widget style config.
                sb.Append(editMode
                    ? $"""<div class="gws-editable" data-gws-widget-id="{Html(widget.Id)}" data-gws-widget-type="{Html(widget.WidgetType)}"><div class="gws-drag-handle" data-gws-drag-handle-for="{Html(widget.Id)}">&#10247;</div>{inner}</div>"""
                    : inner);
            }
            sb.Append("</div>");
        }

        sb.Append("</div></section>\n");
        return sb.ToString();
    }

    // Wraps a widget's rendered HTML in a styled container when it has any per-widget
    // style override set (Phase 6) — otherwise returns the inner HTML untouched, so
    // widgets with no overrides render byte-for-byte as they did before this feature.
    private static string WrapWithStyle(string innerHtml, WidgetStyle style)
    {
        var inlineStyle = style.ToInlineStyle();
        return inlineStyle.Length == 0
            ? innerHtml
            : $"""<div class="gws-widget-style" style="{Html(inlineStyle)}">{innerHtml}</div>""";
    }

    private static string RenderWidget(LayoutWidget widget, string siteSlug, string pageSlug, bool editMode, IReadOnlyList<PublicArticleSummary> articles)
    {
        var p = widget.Props;
        return widget.WidgetType switch
        {
            "hero" => $"""
                <div class="gws-hero gws-align-{Html(Align(p))}">
                  <h1 class="gws-hero-headline"{InlineEditAttrs(editMode, "headline")}>{Html(Get(p, "headline"))}</h1>
                  {(HasValue(p, "subline") ? $"""<p class="gws-hero-subline"{InlineEditAttrs(editMode, "subline")}>{Html(Get(p, "subline"))}</p>""" : "")}
                  <div class="gws-hero-actions">
                    {HeroCta(Get(p, "cta1Label"), Get(p, "cta1Href"), "btn-primary", editMode, "cta1Label")}
                    {HeroCta(Get(p, "cta2Label"), Get(p, "cta2Href"), "btn-ghost", editMode, "cta2Label")}
                  </div>
                </div>
                """,
            "heading" => $"""<{Tag(p)} class="gws-heading gws-align-{Html(Align(p))}"{InlineEditAttrs(editMode, "text")}>{Html(Get(p, "text"))}</{Tag(p)}>""",
            "paragraph" => $"""<p class="gws-paragraph gws-align-{Html(Align(p))}"{InlineEditAttrs(editMode, "text")}>{Html(Get(p, "text"))}</p>""",
            // Same trust boundary as blog articles: only authenticated Contributor/Author/
            // Admin roles can edit Canvas widgets, so rendering Markdown -> HTML here (rather
            // than HTML-encoding it, which would show raw asterisks/brackets) is consistent
            // with how ArticleMarkdownRenderer already treats admin-authored content.
            // Deliberately NOT inline-contenteditable (see BuildEditModeScript's caller) -
            // this prop is Markdown, contenteditable produces HTML, and reconciling
            // HTML-from-contenteditable back into Markdown is a lossy conversion for no
            // real gain. Stays click-to-select -> edit in the Inspector's Markdown textarea.
            "richtext" => $"""<div class="gws-richtext">{Markdown.ToHtml(Get(p, "content"), MarkdownPipeline)}</div>""",
            "button" => $"""
                <div class="gws-button-wrap gws-align-{Html(Align(p))}">
                  <a href="{Html(HrefOrHash(Get(p, "href")))}" class="btn btn-{Html(Get(p, "variant", "primary"))}"{OpenInNewTabAttrs(p)}{InlineEditAttrs(editMode, "label")}>{Html(Get(p, "label"))}</a>
                </div>
                """,
            "image" => HasValue(p, "src")
                ? $"""
                    <div class="gws-image gws-image-{Html(Get(p, "width", "full"))}">
                      <img src="{Html(Get(p, "src"))}" alt="{Html(Get(p, "alt"))}" />
                      {(HasValue(p, "caption") ? $"""<p class="gws-image-caption">{Html(Get(p, "caption"))}</p>""" : "")}
                    </div>
                    """
                : string.Empty,
            "card" => $"""
                <div class="gws-card">
                  {(HasValue(p, "imageSrc") ? $"""<img src="{Html(Get(p, "imageSrc"))}" alt="" class="gws-card-img" />""" : "")}
                  <div class="gws-card-body">
                    <h3 class="gws-card-title">{Html(Get(p, "title"))}</h3>
                    <p class="gws-card-text">{Html(Get(p, "body"))}</p>
                    {(HasValue(p, "link") ? $"""<a href="{Html(Get(p, "link"))}" class="btn btn-sm btn-outline-primary">Read more</a>""" : "")}
                  </div>
                </div>
                """,
            "testimonial" => $"""
                <blockquote class="gws-testimonial">
                  <p class="gws-testimonial-quote">&ldquo;{Html(Get(p, "quote"))}&rdquo;</p>
                  <footer class="gws-testimonial-author">
                    <span class="gws-testimonial-name">{Html(Get(p, "authorName"))}</span>
                    {(HasValue(p, "authorRole") ? $"""<span class="gws-testimonial-role">{Html(Get(p, "authorRole"))}</span>""" : "")}
                  </footer>
                </blockquote>
                """,
            "accordion" => RenderAccordion(Get(p, "itemsJson")),
            "spacer" => $"""<div class="gws-spacer" style="height:{GetInt(p, "height", 48)}px"></div>""",
            "divider" => $"""<hr class="gws-divider gws-divider-{Html(Get(p, "style", "solid"))}" />""",
            "html" => Get(p, "content"),
            "form" => RenderForm(p, siteSlug, pageSlug),
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
    private static string RenderAccordion(string itemsJson)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson) as JsonArray;
            if (node is null || node.Count == 0) return string.Empty;

            var sb = new StringBuilder("""<div class="gws-accordion">""");
            foreach (var item in node.OfType<JsonObject>())
            {
                var question = item["question"]?.GetValue<string>() ?? string.Empty;
                var answer = item["answer"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(question)) continue;

                sb.Append($"""
                    <details class="gws-accordion-item">
                      <summary class="gws-accordion-question">{Html(question)}</summary>
                      <div class="gws-accordion-answer">{Html(answer)}</div>
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
    private static string RenderForm(IReadOnlyDictionary<string, string> p, string siteSlug, string pageSlug)
    {
        var fields = ParseFormFields(Get(p, "fieldsJson"));
        var sb = new StringBuilder();
        sb.Append($"""<form class="gws-form" method="post" action="/cms/{Html(siteSlug)}/submit">""");
        sb.Append($"""<input type="hidden" name="_path" value="{Html(pageSlug)}" />""");

        foreach (var field in fields)
        {
            sb.Append("""<label class="gws-form-field"><span class="gws-form-label">""");
            sb.Append(Html(field.Label));
            if (field.Required) sb.Append("""<span class="gws-form-required">*</span>""");
            sb.Append("</span>");
            sb.Append(RenderFormControl(field));
            sb.Append("</label>");
        }

        sb.Append("""<input type="text" name="company" class="gws-form-honeypot" tabindex="-1" autocomplete="off" />""");
        sb.Append($"""<button type="submit" class="btn btn-primary gws-form-submit">{Html(Get(p, "submitLabel", "Submit"))}</button>""");
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

            return node.OfType<JsonObject>().Select(obj => new FormFieldDefinition(
                Key: obj["key"]?.GetValue<string>() ?? string.Empty,
                Label: obj["label"]?.GetValue<string>() ?? string.Empty,
                Type: obj["type"]?.GetValue<string>() ?? "text",
                Required: obj["required"]?.GetValue<bool>() ?? false,
                OptionsJson: obj["optionsJson"]?.GetValue<string>() ?? string.Empty
            )).Where(f => !string.IsNullOrWhiteSpace(f.Key)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed record FormFieldDefinition(string Key, string Label, string Type, bool Required, string OptionsJson);

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
    private static string InlineEditAttrs(bool editMode, string propKey) =>
        editMode ? $" contenteditable=\"plaintext-only\" data-gws-inline-prop=\"{propKey}\"" : "";

    private static string HrefOrHash(string href) => string.IsNullOrWhiteSpace(href) ? "#" : href;

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
