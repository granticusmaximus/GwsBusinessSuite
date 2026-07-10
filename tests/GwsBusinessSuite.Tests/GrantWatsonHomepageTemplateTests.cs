using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class GrantWatsonHomepageTemplateTests
{
    [Fact]
    public void CreateBlocksJson_ShouldProduceAHomepageWithServiceAndRecentBlogSections()
    {
        var layout = CmsBuilderJson.ParseLayout(GrantWatsonHomepageTemplate.CreateBlocksJson());

        layout.Should().NotBeNull();
        var widgets = layout!.Sections
            .SelectMany(section => section.Columns)
            .SelectMany(column => column.Widgets)
            .ToList();

        widgets.Any(widget =>
                widget.WidgetType == "hero"
                && widget.Props.TryGetValue("cta1Href", out var heroHref)
                && heroHref == "/contact")
            .Should().BeTrue();
        widgets.Any(widget =>
                widget.WidgetType == "card"
                && widget.Props.TryGetValue("title", out var cardTitle)
                && cardTitle == "Inventory and Operations Software")
            .Should().BeTrue();
        widgets.Any(widget =>
                widget.WidgetType == "html"
                && widget.Props.TryGetValue("content", out var htmlContent)
                && htmlContent.Contains("/api/blog", StringComparison.Ordinal)
                && htmlContent.Contains("See all blog articles", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldApplyTemplate_ShouldRecognizeTheLegacyHomepage()
    {
        var legacyPage = new CmsPage
        {
            SiteId = Guid.NewGuid(),
            Title = "Home",
            Slug = "home",
            Status = CmsPageStatuses.Published,
            BlocksJson = """{"sections":[{"id":"9f48f8d200094aae9a1b15328c87f11b","label":"Hero","background":"transparent","padding":"md","columnLayout":"full","columns":[{"id":"4f5289485c444322866d8358f894c994","span":12,"widgets":[{"id":"0955bff91104426fbfc69c5441e8513d","widgetType":"hero","props":{"headline":"Grant Watson","subline":"Developer \u00B7 Builder \u00B7 Creator. I build products that live on the web \u2014 from bespoke CMS tools to AI-powered content pipelines. Always shipping, always learning.","cta1Label":"Read the Blog","cta1Href":"/blog","cta2Label":"Get In Touch","cta2Href":"/contact","align":"left"}},{"id":"359accdf0ad74a19b83ea7280ffd656c","widgetType":"button","props":{"label":"GitHub \u2197","href":"https://github.com/granticusmaximus","variant":"outline-secondary","size":"md","align":"left","openInNewTab":"true"}}]}]}]}"""
        };

        GrantWatsonHomepageTemplate.ShouldApplyTemplate(legacyPage).Should().BeTrue();
    }

    [Fact]
    public void ShouldApplyTemplate_ShouldLeaveCustomHomepageContentAlone()
    {
        var customPage = new CmsPage
        {
            SiteId = Guid.NewGuid(),
            Title = "Home",
            Slug = "home",
            Status = CmsPageStatuses.Published,
            BlocksJson = CmsBuilderJson.Serialize(new PageLayout
            {
                Sections =
                [
                    new LayoutSection
                    {
                        Label = "Custom",
                        Columns =
                        [
                            new LayoutColumn
                            {
                                Widgets =
                                [
                                    new LayoutWidget
                                    {
                                        WidgetType = "hero",
                                        Props = new Dictionary<string, string>
                                        {
                                            ["headline"] = "Custom homepage",
                                            ["cta1Href"] = "/contact"
                                        }
                                    }
                                ]
                            }
                        ]
                    }
                ]
            })
        };

        GrantWatsonHomepageTemplate.ShouldApplyTemplate(customPage).Should().BeFalse();
    }
}
