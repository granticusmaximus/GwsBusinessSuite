using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class NotionMappingTests
{
    [Fact]
    public void MapRichText_ShouldPreserveSupportedAnnotationsAndLink()
    {
        var richText = Json("""
            [{"plain_text":"Hello","href":"https://example.com","annotations":{"bold":true,"italic":true,"strikethrough":true,"code":true,"underline":true,"color":"red"}}]
            """);

        var span = NotionMapping.MapRichText(richText).Should().ContainSingle().Subject;

        span.Text.Should().Be("Hello");
        span.Bold.Should().BeTrue();
        span.Italic.Should().BeTrue();
        span.Strikethrough.Should().BeTrue();
        span.Code.Should().BeTrue();
        span.Link.Should().Be("https://example.com");
    }

    [Theory]
    [InlineData("paragraph", WikiBlockTypes.Paragraph)]
    [InlineData("heading_1", WikiBlockTypes.Heading1)]
    [InlineData("heading_2", WikiBlockTypes.Heading2)]
    [InlineData("heading_3", WikiBlockTypes.Heading3)]
    [InlineData("bulleted_list_item", WikiBlockTypes.BulletedListItem)]
    [InlineData("numbered_list_item", WikiBlockTypes.NumberedListItem)]
    [InlineData("to_do", WikiBlockTypes.ToDo)]
    [InlineData("toggle", WikiBlockTypes.Toggle)]
    [InlineData("quote", WikiBlockTypes.Quote)]
    [InlineData("callout", WikiBlockTypes.Callout)]
    [InlineData("code", WikiBlockTypes.Code)]
    [InlineData("divider", WikiBlockTypes.Divider)]
    [InlineData("image", WikiBlockTypes.Image)]
    [InlineData("video", WikiBlockTypes.Embed)]
    [InlineData("audio", WikiBlockTypes.Embed)]
    [InlineData("file", WikiBlockTypes.Embed)]
    [InlineData("pdf", WikiBlockTypes.Embed)]
    [InlineData("embed", WikiBlockTypes.Embed)]
    [InlineData("bookmark", WikiBlockTypes.Embed)]
    [InlineData("link_preview", WikiBlockTypes.Embed)]
    public void MapBlock_ShouldMapSupportedTypes(string notionType, string expectedType)
    {
        var body = notionType switch
        {
            "divider" => "{}",
            "to_do" => "{\"rich_text\":[{\"plain_text\":\"Text\"}],\"checked\":true}",
            "callout" => "{\"rich_text\":[{\"plain_text\":\"Text\"}],\"icon\":{\"emoji\":\"⚡\"}}",
            "code" => "{\"rich_text\":[{\"plain_text\":\"Text\"}],\"language\":\"csharp\"}",
            "image" or "video" or "audio" or "file" or "pdf" => "{\"external\":{\"url\":\"https://example.com/a\"},\"rich_text\":[]}",
            "embed" or "bookmark" or "link_preview" => "{\"url\":\"https://example.com/a\"}",
            _ => "{\"rich_text\":[{\"plain_text\":\"Text\"}]}"
        };
        var block = Json($"{{\"type\":\"{notionType}\",\"{notionType}\":{body}}}");

        var mapped = NotionMapping.MapBlock(block, 2);

        mapped.Should().NotBeNull();
        mapped!.Type.Should().Be(expectedType);
        mapped.IndentLevel.Should().Be(2);
        if (notionType == "to_do") mapped.Props["checked"].Should().Be("true");
        if (notionType == "callout") mapped.Props["icon"].Should().Be("⚡");
        if (notionType == "code") mapped.Props["language"].Should().Be("csharp");
    }

    [Fact]
    public void MapTable_ShouldCreateNativeTableBlock()
    {
        var table = Json("""{"table":{"has_column_header":true}}""");
        var rows = new[]
        {
            Json("""{"table_row":{"cells":[[{"plain_text":"Name"}],[{"plain_text":"Value"}]]}}"""),
            Json("""{"table_row":{"cells":[[{"plain_text":"A|B"}],[{"plain_text":"Line\nTwo"}]]}}""")
        };

        var mapped = NotionMapping.MapTable(table, rows, 1);

        mapped.Type.Should().Be(WikiBlockTypes.Table);
        mapped.IndentLevel.Should().Be(1);
        mapped.PlainText.Should().Be("| Name | Value |\n| --- | --- |\n| A\\|B | Line Two |\n");
    }

    [Theory]
    [InlineData("column_list", true, false)]
    [InlineData("column", true, false)]
    [InlineData("synced_block", true, false)]
    [InlineData("tab", true, false)]
    [InlineData("template", true, false)]
    [InlineData("child_page", false, true)]
    [InlineData("child_database", false, true)]
    public void WrapperAndTreeClassifications_ShouldBeExplicit(string type, bool isWrapper, bool isTree)
    {
        NotionMapping.IsFlattenedWrapper(type).Should().Be(isWrapper);
        NotionMapping.IsPageTreeBlock(type).Should().Be(isTree);
        NotionMapping.MapBlock(Json($"{{\"type\":\"{type}\",\"{type}\":{{}}}}"), 0).Should().BeNull();
    }

    [Theory]
    [InlineData("title", WikiDatabasePropertyTypes.Title)]
    [InlineData("rich_text", WikiDatabasePropertyTypes.Text)]
    [InlineData("number", WikiDatabasePropertyTypes.Number)]
    [InlineData("select", WikiDatabasePropertyTypes.Select)]
    [InlineData("status", WikiDatabasePropertyTypes.Select)]
    [InlineData("multi_select", WikiDatabasePropertyTypes.MultiSelect)]
    [InlineData("date", WikiDatabasePropertyTypes.Date)]
    [InlineData("checkbox", WikiDatabasePropertyTypes.Checkbox)]
    [InlineData("url", WikiDatabasePropertyTypes.Url)]
    [InlineData("created_time", WikiDatabasePropertyTypes.Date)]
    [InlineData("formula", WikiDatabasePropertyTypes.Text)]
    [InlineData("rollup", WikiDatabasePropertyTypes.Text)]
    [InlineData("relation", WikiDatabasePropertyTypes.Text)]
    [InlineData("place", WikiDatabasePropertyTypes.Text)]
    public void MapPropertyType_ShouldUseSupportedTypesAndTextFallbacks(string notionType, string expectedType) =>
        NotionMapping.MapPropertyType(notionType).Should().Be(expectedType);

    [Theory]
    [InlineData("formula", "{\"type\":\"number\",\"number\":42}", "42")]
    [InlineData("rollup", "{\"type\":\"number\",\"number\":7}", "7")]
    [InlineData("relation", "[{\"id\":\"page-a\"},{\"id\":\"page-b\"}]", "page-a, page-b")]
    public void ApplyPropertyValue_ShouldRenderComplexFallbacksAsText(string notionType, string body, string expected)
    {
        var propertyId = Guid.NewGuid();
        var values = new JsonObject();
        var notionValue = Json($"{{\"type\":\"{notionType}\",\"{notionType}\":{body}}}");

        NotionMapping.ApplyPropertyValue(values, propertyId, WikiDatabasePropertyTypes.Text, notionValue);

        WikiPropertyValues.GetText(values, propertyId).Should().Be(expected);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
