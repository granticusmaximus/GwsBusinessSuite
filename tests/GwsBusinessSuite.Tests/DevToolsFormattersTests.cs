using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

public sealed class DevToolsFormattersTests
{
    [Fact]
    public void FormatJson_ShouldPrettyPrintCompactJson()
    {
        var result = DevToolsFormatters.FormatJson("""{"a":1,"b":[1,2,3]}""");

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\n");
    }

    [Fact]
    public void FormatJson_ShouldFailCleanlyOnMalformedInput()
    {
        var result = DevToolsFormatters.FormatJson("{not valid json");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FormatXml_ShouldIndentAFlatDocument()
    {
        var result = DevToolsFormatters.FormatXml("<root><child>value</child></root>");

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\n");
    }

    [Fact]
    public void FormatXml_ShouldFailCleanlyOnMalformedInput()
    {
        var result = DevToolsFormatters.FormatXml("<root><unclosed></root>");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RenderMarkdownToHtml_ShouldRenderBasicFormattingAndStripRawHtml()
    {
        var html = DevToolsFormatters.RenderMarkdownToHtml("# Title\n\n**bold** and <script>alert(1)</script>");

        html.Should().Contain(">Title</h1>").And.Contain("<strong>bold</strong>");
        html.Should().NotContain("<script>");
    }
}
