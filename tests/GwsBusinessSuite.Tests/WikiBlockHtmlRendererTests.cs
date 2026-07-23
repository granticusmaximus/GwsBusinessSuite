using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiBlockHtmlRendererTests
{
    [Fact]
    public void RenderBlock_ShouldRenderLinkedDatabaseReferenceWithoutCopyingDatabaseContent()
    {
        var databaseId = Guid.NewGuid();
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.LinkedDatabase,
            1,
            [],
            new Dictionary<string, string>
            {
                ["databaseId"] = databaseId.ToString(),
                ["databaseTitle"] = "Projects <2026>"
            });

        var html = WikiBlockHtmlRenderer.RenderBlock(block);

        html.Should().Contain("class=\"wiki-linked-database\"");
        html.Should().Contain($"data-database-id=\"{databaseId}\"");
        html.Should().Contain("Projects &lt;2026&gt;");
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
}
