using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Markdig;

namespace GwsBusinessSuite.Application.CmsBuilder;

/// <summary>
/// Renders a CmsPage's BlocksJson — a PageLayout-shaped Section/Column/Widget document,
/// the same schema the Studio (CmsBuilderEditor.razor) edits — to a public-facing HTML
/// fragment. Mirrors the widget vocabulary and prop-key conventions of the React renderer
/// (CmsBlockRenderer.jsx) and the admin preview (CmsBlockPreview.razor) so all three stay
/// in sync, but this one produces plain HTML strings so it can run outside the Blazor
/// render pipeline, from a minimal API endpoint.
/// </summary>
public static class CmsBlockHtmlRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string Render(string blocksJson, string siteSlug = "", string pageSlug = "")
    {
        var layout = ParseLayout(blocksJson);
        if (layout is null || layout.Sections.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        foreach (var section in layout.Sections)
        {
            html.Append(RenderSection(section, siteSlug, pageSlug));
        }

        return html.ToString();
    }

    private static PageLayout? ParseLayout(string blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PageLayout>(blocksJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string RenderSection(LayoutSection section, string siteSlug, string pageSlug)
    {
        var sectionClass = $"gws-section {BgClass(section.Background)} {PadClass(section.Padding)}".TrimEnd();
        var columnsClass = ColsClass(section.ColumnLayout);

        var sb = new StringBuilder();
        sb.Append($"""<section class="{Html(sectionClass)}"><div class="{Html(columnsClass)}">""");

        foreach (var column in section.Columns)
        {
            sb.Append("""<div class="gws-column">""");
            foreach (var widget in column.Widgets)
            {
                sb.Append(RenderWidget(widget, siteSlug, pageSlug));
            }
            sb.Append("</div>");
        }

        sb.Append("</div></section>\n");
        return sb.ToString();
    }

    private static string RenderWidget(LayoutWidget widget, string siteSlug, string pageSlug)
    {
        var p = widget.Props;
        return widget.WidgetType switch
        {
            "hero" => $"""
                <div class="gws-hero gws-align-{Html(Align(p))}">
                  <h1 class="gws-hero-headline">{Html(Get(p, "headline"))}</h1>
                  {(HasValue(p, "subline") ? $"""<p class="gws-hero-subline">{Html(Get(p, "subline"))}</p>""" : "")}
                  <div class="gws-hero-actions">
                    {HeroCta(Get(p, "cta1Label"), Get(p, "cta1Href"), "btn-primary")}
                    {HeroCta(Get(p, "cta2Label"), Get(p, "cta2Href"), "btn-ghost")}
                  </div>
                </div>
                """,
            "heading" => $"""<{Tag(p)} class="gws-heading gws-align-{Html(Align(p))}">{Html(Get(p, "text"))}</{Tag(p)}>""",
            "paragraph" => $"""<p class="gws-paragraph gws-align-{Html(Align(p))}">{Html(Get(p, "text"))}</p>""",
            "button" => $"""
                <div class="gws-button-wrap gws-align-{Html(Align(p))}">
                  <a href="{Html(HrefOrHash(Get(p, "href")))}" class="btn btn-{Html(Get(p, "variant", "primary"))}"{OpenInNewTabAttrs(p)}>{Html(Get(p, "label"))}</a>
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
            "spacer" => $"""<div class="gws-spacer" style="height:{GetInt(p, "height", 48)}px"></div>""",
            "divider" => $"""<hr class="gws-divider gws-divider-{Html(Get(p, "style", "solid"))}" />""",
            "html" => Get(p, "content"),
            "form" => RenderForm(p, siteSlug, pageSlug),
            _ => string.Empty
        };
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

    private static string HeroCta(string label, string href, string cssClass) =>
        string.IsNullOrWhiteSpace(label)
            ? string.Empty
            : $"""<a href="{Html(HrefOrHash(href))}" class="btn {cssClass}">{Html(label)}</a>""";

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
