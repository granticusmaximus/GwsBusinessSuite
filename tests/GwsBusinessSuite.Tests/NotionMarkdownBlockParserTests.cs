using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using System.Text.Json;

namespace GwsBusinessSuite.Tests;

public sealed class NotionMarkdownBlockParserTests
{
    [Fact]
    public void Parse_ShouldPreserveNotionRichTextListsAndCodeMetadata()
    {
        const string markdown = """
            # Product brief

            A **bold** choice with *emphasis*, ~~removed text~~, `code`, and a [reference](https://example.test).

            - First item
                - Nested item
            1. Ordered item

            ```csharp
            Console.WriteLine("Sentinel");
            ```
            """;

        var blocks = NotionMarkdownBlockParser.Parse(markdown, "Product brief");

        blocks.Should().HaveCount(5);
        blocks.Should().NotContain(block => block.Type == WikiBlockTypes.Heading1);
        var paragraph = blocks[0];
        paragraph.RichText.Should().Contain(span => span.Text == "bold" && span.Bold);
        paragraph.RichText.Should().Contain(span => span.Text == "emphasis" && span.Italic);
        paragraph.RichText.Should().Contain(span => span.Text == "removed text" && span.Strikethrough);
        paragraph.RichText.Should().Contain(span => span.Text == "code" && span.Code);
        paragraph.RichText.Should().Contain(span =>
            span.Text == "reference" && span.Link == "https://example.test");
        blocks[2].Type.Should().Be(WikiBlockTypes.BulletedListItem);
        blocks[2].IndentLevel.Should().Be(1);
        blocks[3].Props["number"].Should().Be("1");
        blocks[4].Type.Should().Be(WikiBlockTypes.Code);
        blocks[4].Props["language"].Should().Be("csharp");
    }

    [Fact]
    public void Parse_ShouldCreateNativeTablesCalloutsTogglesAndAttachmentCards()
    {
        const string markdown = """
            | Name | Status |
            | --- | --- |
            | Launch | **Active** |

            <aside>
            💡 **Remember:** Share the top-level page.
            </aside>

            <details>
            <summary>Deployment notes</summary>
            Hidden **instructions**
            </details>

            [Architecture.pdf](/admin/sentinel/files/65a86a9c-4ce5-4c6c-a36b-d17b671b58b8)
            """;

        var blocks = NotionMarkdownBlockParser.Parse(markdown);

        var table = blocks.Single(block => block.Type == WikiBlockTypes.Table);
        table.PlainText.Should().Be("Name | Status\nLaunch | Active");
        table.Props["hasColumnHeader"].Should().Be("true");
        using var tableJson = JsonDocument.Parse(table.Props["tableJson"]);
        tableJson.RootElement[1][1][0].GetProperty("bold").GetBoolean().Should().BeTrue();

        var callout = blocks.Single(block => block.Type == WikiBlockTypes.Callout);
        callout.Props["icon"].Should().Be("💡");
        callout.RichText.Should().Contain(span => span.Text == "Remember:" && span.Bold);

        var toggle = blocks.Single(block => block.Type == WikiBlockTypes.Toggle);
        toggle.PlainText.Should().Be("Deployment notes");
        blocks.Should().Contain(block =>
            block.IndentLevel == 1
            && block.RichText.Any(span => span.Text == "instructions" && span.Bold));

        var attachment = blocks.Single(block => block.Type == WikiBlockTypes.Embed);
        attachment.Props["fileName"].Should().Be("Architecture.pdf");
        attachment.Props["url"].Should().StartWith("/admin/sentinel/files/");
    }
}
