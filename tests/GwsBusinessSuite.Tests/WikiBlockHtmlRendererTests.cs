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
}
