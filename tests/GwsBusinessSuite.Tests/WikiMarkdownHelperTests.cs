using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class WikiMarkdownHelperTests
{
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
    public void SearchLinkSuggestions_ShouldFilterCaseInsensitively()
    {
        var pages = new List<WikiPage>
        {
            new() { Title = "Deployment Runbook", Slug = "a", Markdown = "" },
            new() { Title = "Onboarding Guide", Slug = "b", Markdown = "" },
            new() { Title = "deployment checklist", Slug = "c", Markdown = "" }
        };

        var result = WikiMarkdownHelper.SearchLinkSuggestions("deploy", pages);

        result.Select(s => s.Title).Should().BeEquivalentTo(["Deployment Runbook", "deployment checklist"]);
        result.Should().OnlyContain(s => s.Id != Guid.Empty);
    }

    [Fact]
    public void SearchLinkSuggestions_ShouldReturnAllUpToMax_WhenQueryIsEmpty()
    {
        var pages = Enumerable.Range(1, 20)
            .Select(i => new WikiPage { Title = $"Page {i}", Slug = $"page-{i}", Markdown = "" })
            .ToList();

        var result = WikiMarkdownHelper.SearchLinkSuggestions(string.Empty, pages, maxResults: 5);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void SearchLinkSuggestions_ShouldRespectMaxResults()
    {
        var pages = Enumerable.Range(1, 10)
            .Select(i => new WikiPage { Title = $"Deploy {i}", Slug = $"deploy-{i}", Markdown = "" })
            .ToList();

        var result = WikiMarkdownHelper.SearchLinkSuggestions("deploy", pages, maxResults: 3);

        result.Should().HaveCount(3);
    }
}
