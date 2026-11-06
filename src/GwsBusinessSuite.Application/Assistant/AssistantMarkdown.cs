using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GwsBusinessSuite.Application.Assistant;

// Model answers are Markdown. They're shown as HTML with raw HTML switched off, links kept only
// when they go to the web, an email address or this app, and images turned into plain links,
// since a model can repeat whatever a page or a pasted text told it to.
public static class AssistantMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    // linksInNewTab: web links open in a new tab (a document read in a dialog, which a link
    // shouldn't replace).
    public static string ToSafeHtml(string markdown, bool linksInNewTab = false)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            link.IsImage = false;
            if (!IsSafeUrl(link.Url)) link.Url = "#";
            if (linksInNewTab && IsWebUrl(link.Url)) NewTab(link);
        }
        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "#";
            if (linksInNewTab && IsWebUrl(link.Url)) NewTab(link);
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        return writer.ToString();
    }

    private static bool IsWebUrl(string? url) =>
        url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private static void NewTab(Inline link)
    {
        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
    }

    private static bool IsSafeUrl(string? url) =>
        string.IsNullOrEmpty(url)
        || url.StartsWith('#')
        || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
}
