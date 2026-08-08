using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiBlockHtmlRendererTests
{
    [Fact]
    public void RenderBlock_ShouldRenderLinkedDatabaseReferenceWithoutCopyingDatabaseContent()
    {
        var databaseId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.LinkedDatabase,
            1,
            [],
            new Dictionary<string, string>
            {
                ["databaseId"] = databaseId.ToString(),
                ["databaseTitle"] = "Projects <2026>",
                ["databaseViewId"] = viewId.ToString(),
                ["databaseViewName"] = "Open & urgent"
            });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        html.Should().Contain("class=\"wiki-linked-database\"");
        html.Should().Contain($"data-database-id=\"{databaseId}\"");
        html.Should().Contain($"data-database-view-id=\"{viewId}\"");
        html.Should().Contain("Projects &lt;2026&gt;");
        html.Should().Contain("Open &amp; urgent");
        html.Should().Contain("margin-left:1.5rem");
        html.Should().NotContain("<2026>");
    }

    [Fact]
    public void PlainTextPreview_ShouldUseLinkedDatabaseTitle()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.LinkedDatabase,
            0,
            [],
            new Dictionary<string, string> { ["databaseTitle"] = "Launch calendar" });

        WikiBlockHtmlRenderer.PlainTextPreview(block).Should().Be("Launch calendar");
        WikiBlockTypes.All.Should().Contain(WikiBlockTypes.LinkedDatabase);
    }

    [Fact]
    public void RenderBlock_ShouldDistinguishInlineDatabaseReferences()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.InlineDatabase,
            0,
            [],
            new Dictionary<string, string>
            {
                ["databaseId"] = Guid.NewGuid().ToString(),
                ["databaseTitle"] = "Tasks"
            });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        html.Should().Contain("wiki-inline-database");
        WikiBlockHtmlRenderer.PlainTextPreview(block).Should().Be("Tasks");
        WikiBlockTypes.All.Should().Contain(WikiBlockTypes.InlineDatabase);
    }

    [Fact]
    public void RenderBlock_ShouldRenderARecognizedEmbedProviderAsASandboxedIframe()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.Embed,
            0,
            [],
            new Dictionary<string, string> { ["url"] = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        html.Should().Contain("wiki-embed-frame");
        html.Should().Contain("data-provider=\"YouTube\"");
        html.Should().Contain("src=\"https://www.youtube.com/embed/dQw4w9WgXcQ\"");
        html.Should().Contain("sandbox=");
    }

    [Fact]
    public void RenderBlock_ShouldFallBackToAPlainLinkForAnUnrecognizedEmbedUrl()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.Embed,
            0,
            [],
            new Dictionary<string, string> { ["url"] = "https://example.com/some-page" });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        html.Should().NotContain("iframe");
        html.Should().Contain("<a href=\"https://example.com/some-page\"");
    }

    [Fact]
    public void RenderPage_ShouldBuildTableOfContentsWithHeadingAnchors()
    {
        var blocks = new[]
        {
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.TableOfContents, 0, [], new Dictionary<string, string>()),
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Heading2, 0, [new WikiRichTextSpan("Release plan")], new Dictionary<string, string>())
        };

        var html = WikiBlockHtmlRenderer.RenderPage(blocks);

        html.Should().Contain("wiki-table-of-contents").And.Contain("Release plan").And.Contain("href=\"#sentinel-heading-1");
        html.Should().Contain("id=\"sentinel-heading-1");
    }

    [Fact]
    public void RenderPage_ShouldPreserveNestedBulletedAndNumberedListStructure()
    {
        var blocks = new[]
        {
            TextBlock(WikiBlockTypes.BulletedListItem, "Parent", 0),
            TextBlock(WikiBlockTypes.BulletedListItem, "Child A", 1),
            TextBlock(WikiBlockTypes.NumberedListItem, "Step one", 2),
            TextBlock(WikiBlockTypes.NumberedListItem, "Step two", 2),
            TextBlock(WikiBlockTypes.BulletedListItem, "Child B", 1),
            TextBlock(WikiBlockTypes.BulletedListItem, "Sibling", 0),
            TextBlock(WikiBlockTypes.Paragraph, "After", 0)
        };

        var html = WikiBlockHtmlRenderer.RenderPage(blocks);

        html.Should().Be(
            "<ul class=\"wiki-list wiki-bulleted-list\">"
            + "<li class=\"wiki-list-item\">Parent"
            + "<ul class=\"wiki-list wiki-bulleted-list\">"
            + "<li class=\"wiki-list-item\">Child A"
            + "<ol class=\"wiki-list wiki-numbered-list\">"
            + "<li class=\"wiki-list-item\">Step one</li>"
            + "<li class=\"wiki-list-item\">Step two</li>"
            + "</ol></li>"
            + "<li class=\"wiki-list-item\">Child B</li>"
            + "</ul></li>"
            + "<li class=\"wiki-list-item\">Sibling</li>"
            + "</ul><p>After</p>");
    }

    [Fact]
    public void RenderPage_ShouldPlaceIndentedDescendantsInsideTheirToggleDetails()
    {
        var blocks = new[]
        {
            TextBlock(WikiBlockTypes.Toggle, "Sources", 0),
            TextBlock(WikiBlockTypes.Paragraph, "Hidden source", 1),
            TextBlock(WikiBlockTypes.Toggle, "More", 1),
            TextBlock(WikiBlockTypes.Paragraph, "Nested source", 2),
            TextBlock(WikiBlockTypes.Paragraph, "Always visible", 0)
        };

        var html = WikiBlockHtmlRenderer.RenderPage(blocks);

        html.Should().Be(
            "<details class=\"wiki-toggle\"><summary>Sources</summary>"
            + "<div class=\"wiki-toggle-content\" style=\"margin-left:1.5rem\">"
            + "<p>Hidden source</p>"
            + "<details class=\"wiki-toggle\"><summary>More</summary>"
            + "<div class=\"wiki-toggle-content\" style=\"margin-left:1.5rem\"><p>Nested source</p></div>"
            + "</details></div></details>"
            + "<p>Always visible</p>");
        html.Should().NotContain("<details open");
    }

    [Theory]
    [InlineData(WikiBlockTypes.Equation, "wiki-equation")]
    [InlineData(WikiBlockTypes.Breadcrumb, "wiki-breadcrumb")]
    [InlineData(WikiBlockTypes.Button, "wiki-button")]
    [InlineData(WikiBlockTypes.SyncedBlock, "wiki-synced-block")]
    public void RenderBlock_ShouldRenderAdvancedNativeBlocks(string type, string expectedClass)
    {
        var block = new WikiBlock(Guid.NewGuid(), type, 0, [new WikiRichTextSpan("Content")], new Dictionary<string, string>());

        WikiBlockHtmlRenderer.RenderBlock(block).Should().Contain(expectedClass);
    }

    [Fact]
    public void RenderBlock_ShouldPreserveRichTableCellsImportedFromNotion()
    {
        var table = NotionMarkdownBlockParser.Parse("""
            | Name | Status |
            | --- | --- |
            | Sentinel | **Active** |
            """).Single();

        var html = WikiBlockHtmlRenderer.RenderBlock(table);

        html.Should().Contain("<th>Name</th>");
        html.Should().Contain("<td>Sentinel</td>");
        html.Should().Contain("<td><b>Active</b></td>");
    }

    [Fact]
    public void RenderRichText_ShouldRenderOnlySupportedSemanticColors()
    {
        var html = WikiBlockHtmlRenderer.RenderRichText(
        [
            new WikiRichTextSpan("Red", TextColor: "red"),
            new WikiRichTextSpan("Highlight", BackgroundColor: "yellow"),
            new WikiRichTextSpan("Unsafe", TextColor: "url(javascript:alert(1))")
        ]);

        html.Should().Contain("wiki-rich-text-color-red");
        html.Should().Contain("wiki-rich-text-bg-yellow");
        html.Should().NotContain("javascript");
    }

    [Fact]
    public void RenderRichText_ShouldRejectExecutableLinksAndKeepSupportedLinks()
    {
        var html = WikiBlockHtmlRenderer.RenderRichText(
        [
            new WikiRichTextSpan("Unsafe", Link: "javascript:alert(1)"),
            new WikiRichTextSpan("Web", Link: "https://example.com/docs"),
            new WikiRichTextSpan("Page", Link: $"wikilink:{Guid.NewGuid()}")
        ]);

        html.Should().NotContain("javascript:");
        html.Should().Contain("href=\"https://example.com/docs\"");
        html.Should().Contain("href=\"wikilink:");
        html.Should().StartWith("Unsafe");
    }

    [Theory]
    [InlineData(WikiBlockTypes.Image)]
    [InlineData(WikiBlockTypes.Embed)]
    public void RenderBlock_ShouldRejectExecutableMediaUrls(string blockType)
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            blockType,
            0,
            [new WikiRichTextSpan("Unsafe")],
            new Dictionary<string, string> { ["url"] = "javascript:alert(1)" });

        WikiBlockHtmlRenderer.RenderBlock(block).Should().BeEmpty();
    }

    [Fact]
    public void RenderBlock_ShouldFallBackToANonExecutableButtonTarget()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.Button,
            0,
            [new WikiRichTextSpan("Run")],
            new Dictionary<string, string> { ["url"] = "javascript:alert(1)" });

        WikiBlockHtmlRenderer.RenderBlock(block)
            .Should().Contain("href=\"#\"")
            .And.NotContain("javascript:");
    }

    [Fact]
    public void RenderBlock_ShouldRenderInteractiveTabsWithEncodedLabelsAndRichText()
    {
        var tabs = new[]
        {
            new WikiTab("Overview <script>", [new WikiRichTextSpan("Summary", Bold: true)]),
            new WikiTab("Evidence", [new WikiRichTextSpan("Source", Link: "https://example.com/source")])
        };
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.Tab,
            1,
            [],
            new Dictionary<string, string>
            {
                ["tabsJson"] = JsonSerializer.Serialize(tabs, WikiBlockJson.Options)
            });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        WikiBlockTypes.All.Should().Contain(WikiBlockTypes.Tab);
        html.Should().Contain("class=\"wiki-tabs\"");
        html.Should().Contain("Overview &lt;script&gt;").And.NotContain("<script>");
        html.Should().Contain("<b>Summary</b>");
        html.Should().Contain("href=\"https://example.com/source\"");
        html.Should().Contain("type=\"radio\"").And.Contain(" checked");
        html.Should().Contain("role=\"region\"");
        html.Should().Contain("margin-left:1.5rem");
        html.Split("class=\"wiki-tab-panel\"").Should().HaveCount(3);
        WikiBlockHtmlRenderer.PlainTextPreview(block).Should().Contain("Overview <script>: Summary");
    }

    [Fact]
    public void RenderBlock_ShouldFallBackToPlainTextTabsWhenStructuredPropsAreInvalid()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.Tab,
            0,
            [new WikiRichTextSpan("First pane ||| Second <pane>")],
            new Dictionary<string, string> { ["tabsJson"] = "not-json" });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        html.Should().Contain("Tab 1").And.Contain("First pane");
        html.Should().Contain("Tab 2").And.Contain("Second &lt;pane&gt;");
        html.Should().NotContain("Second <pane>");
    }

    private static WikiBlock TextBlock(string type, string text, int indentLevel) =>
        new(
            Guid.NewGuid(),
            type,
            indentLevel,
            [new WikiRichTextSpan(text)],
            new Dictionary<string, string>());
}
