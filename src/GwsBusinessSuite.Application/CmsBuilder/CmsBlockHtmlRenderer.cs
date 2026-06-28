using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Markdig;

namespace GwsBusinessSuite.Application.CmsBuilder;

/// <summary>
/// Renders a CmsPage's BlocksJson to a public-facing HTML fragment. Mirrors the block
/// vocabulary of the admin-only Blazor preview (CmsBlockPreview.razor) so what an editor
/// sees while building a page matches what visitors see once it's published, but this
/// renderer produces plain HTML strings so it can run outside the Blazor render pipeline,
/// from a minimal API endpoint.
/// </summary>
public static class CmsBlockHtmlRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string Render(string blocksJson)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(blocksJson) ? "[]" : blocksJson.Trim());
        var blocks = (node as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

        var html = new StringBuilder();
        foreach (var block in blocks)
        {
            html.Append("<section class=\"cms-block\">").Append(RenderBlock(block)).Append("</section>\n");
        }

        return html.ToString();
    }

    private static string RenderBlock(JsonObject block)
    {
        var type = GetString(block, "type");
        return type switch
        {
            "hero" => $"""
                <div class="cms-hero">
                  <h1>{Html(GetString(block, "title", "New hero section"))}</h1>
                  <p>{Html(GetString(block, "subtitle"))}</p>
                  {Button(GetString(block, "primaryCta"), GetString(block, "primaryCtaHref", "#"))}
                </div>
                """,
            "proof-grid" => $"""
                <h2>{Html(GetString(block, "title", "Proof points"))}</h2>
                <div class="cms-grid">{Items(block, "items")}</div>
                """,
            "feature-stack" => $"""
                <h2>{Html(GetString(block, "title", "What you get"))}</h2>
                <ul class="cms-list">{ListItems(block, "items")}</ul>
                """,
            "cta" => $"""
                <div class="cms-callout">
                  <h2>{Html(GetString(block, "title", "Ready to take action?"))}</h2>
                  {Button(GetString(block, "button", "Continue"), GetString(block, "buttonHref", "#"))}
                </div>
                """,
            "article-header" => $"""
                <h1>{Html(GetString(block, "title", "Editorial title"))}</h1>
                <p>{Html(GetString(block, "subtitle"))}</p>
                """,
            // toc/faq/testimonials render heading-only deliberately: the admin preview's
            // body content for these three ("Introduction/Main section/...", "Question and
            // answer content.", "Use this block to add social proof...") is editorial
            // scaffolding text, not real per-block data — there's no "items" field for any
            // of them in any workflow blueprint. Copying that placeholder copy to a public
            // page would look like an obviously broken stub to real visitors, which is worse
            // than just showing the heading. countdown and contact-form below DO have real
            // per-block data (days / static form fields) and are rendered in full.
            "toc" => $"""
                <h2>{Html(GetString(block, "title", "In this article"))}</h2>
                """,
            "rich-content" => $"""
                <div class="cms-rich-content">{Markdown.ToHtml(GetString(block, "body", string.Empty), MarkdownPipeline)}</div>
                """,
            "author-box" => $"""
                <div class="cms-author">
                  <div class="cms-author-name">{Html(GetString(block, "name", string.Empty))}</div>
                  <div class="cms-author-role">{Html(GetString(block, "role", string.Empty))}</div>
                </div>
                """,
            "newsletter-cta" => $"""
                <div class="cms-callout">
                  <h2>{Html(GetString(block, "title", "Get weekly updates"))}</h2>
                  {Button(GetString(block, "button", "Subscribe"), GetString(block, "buttonHref", "#"))}
                </div>
                """,
            "countdown" => $"""
                <div class="cms-countdown">
                  <h2>{Html(GetString(block, "title", "Countdown"))}</h2>
                  <div class="cms-countdown-days">{GetInt(block, "days", 7)}</div>
                  <div class="cms-countdown-caption">days remaining</div>
                </div>
                """,
            "pricing-table" => $"""
                <h2>{Html(GetString(block, "title", "Pricing"))}</h2>
                <div class="cms-grid">{Items(block, "plans")}</div>
                """,
            "faq" => $"""
                <h2>{Html(GetString(block, "title", "Frequently asked questions"))}</h2>
                """,
            "service-list" => $"""
                <h2>{Html(GetString(block, "title", "Services"))}</h2>
                <div class="cms-grid">{Items(block, "items")}</div>
                """,
            "testimonials" => $"""
                <h2>{Html(GetString(block, "title", "Client results"))}</h2>
                """,
            // Visual structure only — there's no submission endpoint behind this yet, same
            // as the admin preview's mockup inputs. A page author who drags this block in
            // gets the layout they designed rather than nothing; wiring up real submission
            // handling is a separate, larger piece of work.
            "contact-form" => $"""
                <div class="cms-callout">
                  <h2>{Html(GetString(block, "title", "Get your plan"))}</h2>
                  <div class="cms-form-grid">
                    <input type="text" placeholder="Name" disabled />
                    <input type="email" placeholder="Email" disabled />
                    <textarea rows="3" placeholder="Project details" disabled></textarea>
                  </div>
                </div>
                """,
            "image" => GetString(block, "src") is { Length: > 0 } src
                ? $"""<img class="cms-image" src="{Html(src)}" alt="{Html(GetString(block, "alt", string.Empty))}" />"""
                : string.Empty,
            _ => string.Empty
        };
    }

    private static string Button(string label, string href) =>
        string.IsNullOrWhiteSpace(label)
            ? string.Empty
            : $"""<a class="cms-button" href="{Html(string.IsNullOrWhiteSpace(href) ? "#" : href)}">{Html(label)}</a>""";

    private static string Items(JsonObject block, string propertyName) =>
        string.Concat(GetStringArray(block, propertyName).Select(item => $"""<div class="cms-grid-item">{Html(item)}</div>"""));

    private static string ListItems(JsonObject block, string propertyName) =>
        string.Concat(GetStringArray(block, propertyName).Select(item => $"<li>{Html(item)}</li>"));

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static int GetInt(JsonObject block, string propertyName, int fallback)
    {
        if (block[propertyName] is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        return fallback;
    }

    private static string GetString(JsonObject block, string propertyName, string fallback = "")
    {
        if (block[propertyName] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        return fallback;
    }

    private static IReadOnlyList<string> GetStringArray(JsonObject block, string propertyName)
    {
        if (block[propertyName] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}
