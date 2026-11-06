using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using UglyToad.PdfPig;

namespace GwsBusinessSuite.Tests;

// Markdown as a PDF (06-11-26): World News' event document downloads as a PDF made from the
// Markdown the page shows rendered.
public sealed class MarkdownPdfTests
{
    [Fact]
    public void Render_ShouldMakeAPdfOfTheMarkdown_WithItsTextAndLinks()
    {
        const string markdown = """
            # Istanbul floods kill 64

            - Outlets: 3, reports: 3
            - Languages: English, Japanese

            ## What the sources say

            Floods **killed** 64 _people_ in `Istanbul`.

            1. First point
            2. Second point

            > A quoted description from the outlet.

            ---

            ### NHK: Japan, Japanese, 20-10-26 14:00

            [イスタンブールで洪水](https://example.com/nhk) and [a script link](javascript:alert(1))
            """;

        var pdf = MarkdownPdf.Render(markdown, "World News · made 06-11-26 10:00");

        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
        text.Should().Contain("Istanbul").And.Contain("sources").And.Contain("killed").And.Contain("Second")
            .And.Contain("quoted").And.Contain("NHK:").And.Contain("made").And.Contain("1 / 1");
        var links = document.GetPages().SelectMany(page => page.GetHyperlinks()).Select(link => link.Uri).ToList();
        links.Should().Equal(["https://example.com/nhk"], "only web and mail links stay links");
    }

    [Fact]
    public void Render_ShouldDrawATable()
    {
        const string markdown = """
            **Who reports what**:

            | Outlet | 1 | 2 |
            | --- | :-: | :-: |
            | Reuters | ● | – |
            | Al Jazeera | ● | ● |
            """;

        using var document = PdfDocument.Open(MarkdownPdf.Render(markdown, "test"));
        var words = document.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text).ToList();
        words.Should().Contain(["Outlet", "Reuters", "Al", "Jazeera"]).And.NotContain(word => word.Contains('|'), "it's a table, not the Markdown");
    }
}
