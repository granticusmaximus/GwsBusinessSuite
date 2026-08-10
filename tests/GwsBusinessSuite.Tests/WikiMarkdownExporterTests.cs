using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiMarkdownExporterTests
{
    [Fact]
    public void ExportPage_ShouldRenderCommonBlockTypesAsPlainMarkdown()
    {
        var blocks = new List<WikiBlock>
        {
            new(Guid.NewGuid(), WikiBlockTypes.Heading1, 0, [new WikiRichTextSpan("Runbook")], new Dictionary<string, string>()),
            new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Plain "), new WikiRichTextSpan("bold", Bold: true)], new Dictionary<string, string>()),
            new(Guid.NewGuid(), WikiBlockTypes.BulletedListItem, 0, [new WikiRichTextSpan("First")], new Dictionary<string, string>()),
            new(Guid.NewGuid(), WikiBlockTypes.ToDo, 0, [new WikiRichTextSpan("Done item")], new Dictionary<string, string> { ["checked"] = "true" }),
            new(Guid.NewGuid(), WikiBlockTypes.Code, 0, [new WikiRichTextSpan("const x = 1;")], new Dictionary<string, string> { ["language"] = "javascript" }),
            new(Guid.NewGuid(), WikiBlockTypes.Divider, 0, [], new Dictionary<string, string>())
        };

        var markdown = WikiMarkdownExporter.ExportPage("Deploy runbook", blocks);

        markdown.Should().Contain("# Deploy runbook");
        markdown.Should().Contain("# Runbook");
        markdown.Should().Contain("Plain **bold**");
        markdown.Should().Contain("- First");
        markdown.Should().Contain("- [x] Done item");
        markdown.Should().Contain("```javascript\nconst x = 1;\n```");
        markdown.Should().Contain("---");
    }

    [Fact]
    public void ExportPage_ShouldWrapToggleChildrenInADetailsElementAndCloseItWhenIndentDrops()
    {
        var blocks = new List<WikiBlock>
        {
            new(Guid.NewGuid(), WikiBlockTypes.Toggle, 0, [new WikiRichTextSpan("More detail")], new Dictionary<string, string>()),
            new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 1, [new WikiRichTextSpan("Hidden content")], new Dictionary<string, string>()),
            new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Back at top level")], new Dictionary<string, string>())
        };

        var markdown = WikiMarkdownExporter.ExportPage("Page", blocks);

        markdown.Should().Contain("<details>\n<summary>More detail</summary>");
        // The closing tag must land BEFORE the sibling paragraph that dropped back to indent 0,
        // not after it - otherwise "Back at top level" would render as (still-collapsed) toggle
        // content instead of a normal visible paragraph.
        var detailsCloseIndex = markdown.IndexOf("</details>", StringComparison.Ordinal);
        var siblingIndex = markdown.IndexOf("Back at top level", StringComparison.Ordinal);
        detailsCloseIndex.Should().BeGreaterThan(0);
        detailsCloseIndex.Should().BeLessThan(siblingIndex);
        markdown.Should().Contain("Hidden content");
    }

    [Fact]
    public void ExportPage_ShouldRenderATableFromTableJsonAsAMarkdownTable()
    {
        var block = new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.Table,
            0,
            [],
            new Dictionary<string, string>
            {
                ["tableJson"] = System.Text.Json.JsonSerializer.Serialize(
                    new List<List<List<WikiRichTextSpan>>>
                    {
                        new()
                        {
                            new List<WikiRichTextSpan> { new("Name") },
                            new List<WikiRichTextSpan> { new("Status") }
                        },
                        new()
                        {
                            new List<WikiRichTextSpan> { new("Sentinel") },
                            new List<WikiRichTextSpan> { new("Active") }
                        }
                    },
                    WikiBlockJson.Options)
            });

        var markdown = WikiMarkdownExporter.ExportPage("Page", [block]);

        markdown.Should().Contain("| Name | Status |");
        markdown.Should().Contain("| --- | --- |");
        markdown.Should().Contain("| Sentinel | Active |");
    }
}
