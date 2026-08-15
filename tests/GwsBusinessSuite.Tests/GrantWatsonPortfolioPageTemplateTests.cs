using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class GrantWatsonPortfolioPageTemplateTests
{
    private static readonly string[] ExpectedProjectNames =
    [
        "GwsBusinessSuite",
        "GrantOS.Sentinel",
        "GwsMeet",
        "GWS-Connect",
        "PodcastDirectory",
        "piping-wheel",
        "OurWish",
        "Watsyn-Jarvis",
        "React-Workboard-with-Authentication",
        "rocket-chatter",
        "React-and-Firebase-Authentication-Boilerplate"
    ];

    [Fact]
    public void CreateBlocksJson_ShouldProduceAtLeastTenProjectCards_EachLinkingToItsGitHubRepo()
    {
        var layout = CmsBuilderJson.ParseLayout(GrantWatsonPortfolioPageTemplate.CreateBlocksJson());
        layout.Should().NotBeNull();

        var cards = layout!.Sections
            .SelectMany(section => section.Columns)
            .SelectMany(column => column.Widgets)
            .Where(widget => widget.WidgetType == "card")
            .ToList();

        cards.Should().HaveCountGreaterThanOrEqualTo(10);
        cards.Select(card => card.Props["title"]).Should().Contain(ExpectedProjectNames);

        foreach (var card in cards)
        {
            card.Props["body"].Should().Contain(
                "https://github.com/granticusmaximus/", $"the card for \"{card.Props["title"]}\" should link to its real repo");
        }
    }

    [Fact]
    public void CreateBlocksJson_ShouldIncludeTheGitHubContributionGraphEmbed()
    {
        var layout = CmsBuilderJson.ParseLayout(GrantWatsonPortfolioPageTemplate.CreateBlocksJson());

        var htmlWidgets = layout!.Sections
            .SelectMany(section => section.Columns)
            .SelectMany(column => column.Widgets)
            .Where(widget => widget.WidgetType == "html")
            .ToList();

        htmlWidgets.Should().ContainSingle(widget =>
            widget.Props["content"].Contains("ghchart.rshah.org/f59e0b/granticusmaximus", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBlocksJson_ShouldLinkTheHeroCtaToTheGitHubProfile()
    {
        var layout = CmsBuilderJson.ParseLayout(GrantWatsonPortfolioPageTemplate.CreateBlocksJson());

        var hero = layout!.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets)
            .Single(widget => widget.WidgetType == "hero");

        hero.Props["cta1Href"].Should().Be("https://github.com/granticusmaximus");
    }

    [Fact]
    public void CreateBlocksJson_ShouldRenderWithoutThrowing_ThroughTheRealPublicRenderer()
    {
        var html = CmsBlockHtmlRenderer.Render(GrantWatsonPortfolioPageTemplate.CreateBlocksJson(), siteSlug: "grantwatson-dev", pageSlug: "portfolio");

        html.Should().Contain("Portfolio");
        html.Should().Contain("GwsBusinessSuite");
        html.Should().Contain("ghchart.rshah.org");
    }
}
