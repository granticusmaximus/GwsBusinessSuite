using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

// Sentinel item 17, diagram blocks: the server side (rendering, Markdown export/import, previews).
// Drawing happens in the browser - see WikiDiagramBrowserTests.
public sealed class WikiDiagramBlockTests
{
    private const string Flowchart = "graph TD\n  A[Start] --> B{OK?}\n  B -->|yes| C[Ship]";

    private static WikiBlock Diagram(string source) =>
        new(Guid.NewGuid(), WikiBlockTypes.Diagram, 0, [new WikiRichTextSpan(source)], new Dictionary<string, string>());

    [Fact]
    public void Render_ShouldEmitAHydrationTarget_WithTheSourceEncoded()
    {
        var html = WikiBlockHtmlRenderer.RenderBlock(Diagram("graph TD\n A[\"<script>alert(1)</script>\"] --> B"));

        html.Should().StartWith("<div class=\"wiki-diagram wiki-mermaid-target\" data-mermaid=\"graph TD\n A[&quot;&lt;script&gt;alert(1)&lt;/script&gt;&quot;] --&gt; B\">");
        html.Should().Contain("<pre class=\"wiki-code\"><code>graph TD").And.NotContain("<script>");
        WikiBlockTypes.All.Should().Contain(WikiBlockTypes.Diagram);
    }

    [Fact]
    public void Markdown_ShouldExportAMermaidFence_ThatImportsBackAsADiagram()
    {
        var markdown = WikiMarkdownExporter.ExportPage("Release", [Diagram(Flowchart)]);

        markdown.Should().Contain("```mermaid\n" + Flowchart + "\n```");
        var imported = NotionMarkdownBlockParser.Parse(markdown, "Release");
        var diagram = imported.Should().ContainSingle(b => b.Type == WikiBlockTypes.Diagram).Subject;
        diagram.PlainText.Should().Be(Flowchart);
        NotionMarkdownBlockParser.Parse("```csharp\nvar x = 1;\n```").Should().ContainSingle()
            .Which.Type.Should().Be(WikiBlockTypes.Code, "other fences stay code blocks");
    }

    [Fact]
    public void HoverPreview_ShouldNameTheDiagramRatherThanShowItsSource()
    {
        var page = new WikiPage { Title = "Release", Slug = "release" };

        SentinelPagePreviewBuilder.Build(page, [Diagram(Flowchart)], DateTimeOffset.UtcNow).Lines
            .Should().Equal(new SentinelPreviewLine("media", "Diagram"));
    }
}
