using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Markdig;

namespace GwsBusinessSuite.Application.DevTools;

public static class DevToolsFormatters
{
    private static readonly JsonSerializerOptions PrettyPrintOptions = new() { WriteIndented = true };

    // Matches the "untrusted input" Markdig shape used everywhere else in this app that renders
    // freely-typed Markdown (ArticleMarkdownRenderer, CmsBlockHtmlRenderer, etc.) - raw HTML is
    // stripped since this tool has no reason to trust whatever a user pastes in.
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static DevToolsResult FormatJson(string input)
    {
        try
        {
            var node = JsonNode.Parse(input);
            return DevToolsResult.Ok(node?.ToJsonString(PrettyPrintOptions) ?? "null");
        }
        catch (JsonException ex)
        {
            return DevToolsResult.Fail($"Invalid JSON: {ex.Message}");
        }
    }

    public static DevToolsResult FormatXml(string input)
    {
        try
        {
            var document = XDocument.Parse(input);
            using var writer = new StringWriter();
            document.Save(writer);
            return DevToolsResult.Ok(writer.ToString());
        }
        catch (XmlException ex)
        {
            return DevToolsResult.Fail($"Invalid XML: {ex.Message}");
        }
    }

    public static string RenderMarkdownToHtml(string input) => Markdown.ToHtml(input, MarkdownPipeline);
}
