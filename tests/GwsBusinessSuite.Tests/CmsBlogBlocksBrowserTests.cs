using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class CmsBlogBlocksBrowserTests(PlaywrightBrowserFixture fixture)
{
    [Theory]
    [InlineData("cms-public.css", 360)]
    [InlineData("cms-public.css", 1280)]
    [InlineData("public-site.css", 360)]
    [InlineData("public-site.css", 1280)]
    public async Task BlogBlocks_ShouldRemainReadableAndFitTheViewport(string stylesheet, int width)
    {
        var widgets = new List<LayoutWidget>();
        foreach (var presentation in new[] { "grid", "list", "classic", "overlay" })
        {
            widgets.Add(new LayoutWidget
            {
                WidgetType = "posts-grid",
                Props = new() { ["layout"] = presentation, ["count"] = "2" }
            });
        }
        widgets.Add(new LayoutWidget
        {
            WidgetType = "related-posts", Props = new() { ["sourceArticleSlug"] = "anchor", ["count"] = "6" }
        });
        var layout = new PageLayout
        {
            Sections = [new LayoutSection { Columns = [new LayoutColumn { Widgets = widgets }] }]
        };
        var articles = new PublicArticleSummary[]
        {
            new("first", "A practical guide to building useful applications", "A short summary for the layout preview.", null, null),
            new("second", "Working with published content", "Another short summary.", null, null)
        };
        var related = new Dictionary<string, IReadOnlyList<RelatedArticleView>>
        {
            ["anchor"] = Enumerable.Range(1, 6).Select(i => new RelatedArticleView($"Recommended article {i}", $"related-{i}")).ToList()
        };
        var cssPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot", stylesheet));
        var css = await File.ReadAllTextAsync(cssPath);
        var html = CmsBlockHtmlRenderer.Render(layout, articles: articles, relatedPostsByAnchorSlug: related);
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 900 } });
        await page.SetContentAsync($"<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>{css}</style></head><body>{html}</body></html>");

        (await page.Locator(".gws-related-posts-item").CountAsync()).Should().Be(6);
        (await page.Locator("a[href='/blog/related-6']").IsVisibleAsync()).Should().BeTrue();
        (await page.Locator("a[href='/blog/first']").CountAsync()).Should().Be(4);
        var overflow = await page.EvaluateAsync<double>("document.documentElement.scrollWidth - window.innerWidth");
        overflow.Should().BeLessThanOrEqualTo(1, "all blog layouts must fit mobile and desktop viewports");
        foreach (var selector in new[] { ".gws-posts-grid-list-item", ".gws-posts-grid-classic-item", ".gws-posts-grid-overlay-item", ".gws-related-posts-item" })
        {
            var box = await page.Locator(selector).First.BoundingBoxAsync();
            box.Should().NotBeNull();
            box!.Width.Should().BeGreaterThan(0);
            box.Height.Should().BeGreaterThan(0);
        }
    }
}
