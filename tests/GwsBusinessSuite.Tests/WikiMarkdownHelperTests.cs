using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Markdig;

namespace GwsBusinessSuite.Tests;

public sealed class WikiMarkdownHelperTests
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    [Fact]
    public void ResolveWikiLinks_ShouldRewriteToWikilinkHref_WhenTitleMatchesAnExistingPage()
    {
        var pages = new List<WikiPage> { new() { Title = "Deployment Runbook", Slug = "deployment-runbook", Markdown = "" } };
        var target = pages[0];

        var result = WikiMarkdownHelper.ResolveWikiLinks("See [[Deployment Runbook]] for details.", pages);

        result.Should().Be($"See [Deployment Runbook](wikilink:{target.Id}) for details.");
    }

    [Fact]
    public void ResolveWikiLinks_ShouldMatchCaseInsensitively()
    {
        var pages = new List<WikiPage> { new() { Title = "Deployment Runbook", Slug = "deployment-runbook", Markdown = "" } };

        var result = WikiMarkdownHelper.ResolveWikiLinks("[[deployment runbook]]", pages);

        result.Should().Contain("wikilink:");
    }

    [Fact]
    public void ResolveWikiLinks_ShouldRenderPlainEmphasis_WhenNoPageMatches()
    {
        var result = WikiMarkdownHelper.ResolveWikiLinks("See [[Nonexistent Page]] for details.", []);

        result.Should().Be("See *Nonexistent Page* _(wiki page not found)_ for details.");
    }

    [Fact]
    public void ResolveWikiLinks_ShouldReturnEmpty_WhenMarkdownIsEmpty()
    {
        WikiMarkdownHelper.ResolveWikiLinks("", []).Should().Be("");
    }

    [Fact]
    public void BuildTableOfContentsMarkdown_ShouldListHeadings_WithMatchingAnchorIds()
    {
        const string markdown = """
            # Title

            ## First Section

            Some text.

            ### A Subsection

            More text.

            ## Second Section
            """;

        var toc = WikiMarkdownHelper.BuildTableOfContentsMarkdown(markdown, Pipeline);

        toc.Should().Contain("[First Section](#first-section)");
        toc.Should().Contain("[Second Section](#second-section)");
        toc.Should().Contain("[A Subsection](#a-subsection)");

        // The generated anchors must match what the real renderer assigns via
        // UseAutoIdentifiers(), since that's what the TOC links have to jump to.
        var renderedHtml = Markdown.ToHtml(markdown, Pipeline);
        renderedHtml.Should().Contain("id=\"first-section\"");
        renderedHtml.Should().Contain("id=\"second-section\"");
        renderedHtml.Should().Contain("id=\"a-subsection\"");
    }

    [Fact]
    public void BuildTableOfContentsMarkdown_ShouldIndentSubheadings_RelativeToTopLevel()
    {
        const string markdown = """
            ## Top
            ### Nested
            """;

        var toc = WikiMarkdownHelper.BuildTableOfContentsMarkdown(markdown, Pipeline);
        var lines = toc.Split('\n');

        lines[0].Should().StartWith("- [Top]");
        lines[1].Should().StartWith("  - [Nested]");
    }

    [Fact]
    public void BuildTableOfContentsMarkdown_ShouldExcludeH1_ButIncludeH2ThroughH4()
    {
        const string markdown = """
            # PageTitle
            ## H2
            ### H3
            #### H4
            ##### H5
            """;

        var toc = WikiMarkdownHelper.BuildTableOfContentsMarkdown(markdown, Pipeline);

        toc.Should().NotContain("PageTitle");
        toc.Should().Contain("H2");
        toc.Should().Contain("H3");
        toc.Should().Contain("H4");
        toc.Should().NotContain("H5");
    }

    [Fact]
    public void BuildTableOfContentsMarkdown_ShouldReturnEmpty_WhenNoHeadingsPresent()
    {
        WikiMarkdownHelper.BuildTableOfContentsMarkdown("Just a paragraph, no headings.", Pipeline).Should().BeEmpty();
    }

    [Fact]
    public void SearchPageTitles_ShouldFilterCaseInsensitively()
    {
        var pages = new List<WikiPage>
        {
            new() { Title = "Deployment Runbook", Slug = "a", Markdown = "" },
            new() { Title = "Onboarding Guide", Slug = "b", Markdown = "" },
            new() { Title = "deployment checklist", Slug = "c", Markdown = "" }
        };

        var result = WikiMarkdownHelper.SearchPageTitles("deploy", pages);

        result.Should().BeEquivalentTo(["Deployment Runbook", "deployment checklist"]);
    }

    [Fact]
    public void SearchPageTitles_ShouldReturnAllUpToMax_WhenQueryIsEmpty()
    {
        var pages = Enumerable.Range(1, 20)
            .Select(i => new WikiPage { Title = $"Page {i}", Slug = $"page-{i}", Markdown = "" })
            .ToList();

        var result = WikiMarkdownHelper.SearchPageTitles(string.Empty, pages, maxResults: 5);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void SearchPageTitles_ShouldRespectMaxResults()
    {
        var pages = Enumerable.Range(1, 10)
            .Select(i => new WikiPage { Title = $"Deploy {i}", Slug = $"deploy-{i}", Markdown = "" })
            .ToList();

        var result = WikiMarkdownHelper.SearchPageTitles("deploy", pages, maxResults: 3);

        result.Should().HaveCount(3);
    }
}
