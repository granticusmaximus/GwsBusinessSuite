using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Workstream C, Tier 3 (portfolio/project grid) - BuildPortfolioRuntimeScript's filter and
// detail-overlay behavior needs a real browser: filtering hides/shows real DOM elements, and the
// overlay is built dynamically and appended to <body>, neither of which a static-shell unit test
// can verify.
[Collection("Playwright")]
public sealed class CmsPortfolioGridBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static async Task<string> ReadCssAsync(string stylesheet)
    {
        var cssPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot", stylesheet));
        return await File.ReadAllTextAsync(cssPath);
    }

    private static string BuildHtml(string css)
    {
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Widgets =
                            [
                                new LayoutWidget
                                {
                                    WidgetType = "portfolio-grid",
                                    Props = new()
                                    {
                                        ["columns"] = "3",
                                        ["itemsJson"] = """
                                            [
                                              {"title":"Web Project","category":"Web","imageUrl":"/media/web.jpg","description":"A web build.","tags":"react, node","linkUrl":"https://web.test"},
                                              {"title":"Mobile Project","category":"Mobile","imageUrl":"/media/mobile.jpg","description":"A mobile build.","tags":"swift"}
                                            ]
                                            """
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        var bodyHtml = CmsBlockHtmlRenderer.Render(layout);
        return $"""
            <!doctype html>
            <html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>{css}</style></head>
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildPortfolioRuntimeScript()}</body></html>
            """;
    }

    [Fact]
    public async Task Portfolio_ShouldShowBothItems_BeforeAnyFilterClick()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        (await page.Locator(".gws-portfolio-item:visible").CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Portfolio_ClickingAFilter_ShouldHideNonMatchingItems()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator("[data-gws-portfolio-filter='Mobile']").ClickAsync();

        (await page.Locator(".gws-portfolio-item:visible").CountAsync()).Should().Be(1);
        (await page.Locator(".gws-portfolio-item:visible .gws-portfolio-item-title").InnerTextAsync()).Should().Be("Mobile Project");
    }

    [Fact]
    public async Task Portfolio_ClickingAllAfterFiltering_ShouldShowBothItemsAgain()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator("[data-gws-portfolio-filter='Mobile']").ClickAsync();
        await page.Locator("[data-gws-portfolio-filter='all']").ClickAsync();

        (await page.Locator(".gws-portfolio-item:visible").CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Portfolio_ClickingAnItem_ShouldOpenTheDetailOverlayWithItsData()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-portfolio-item", new PageLocatorOptions { HasTextString = "Web Project" }).ClickAsync();

        (await page.Locator(".gws-portfolio-lightbox").IsVisibleAsync()).Should().BeTrue();
        (await page.Locator(".gws-portfolio-lightbox-title").InnerTextAsync()).Should().Be("Web Project");
        (await page.Locator(".gws-portfolio-lightbox-description").InnerTextAsync()).Should().Be("A web build.");
        (await page.Locator(".gws-portfolio-lightbox-tag").CountAsync()).Should().Be(2);
        (await page.Locator(".gws-portfolio-lightbox-link").GetAttributeAsync("href")).Should().Be("https://web.test");
    }

    [Fact]
    public async Task Portfolio_ClickingTheCloseButton_ShouldHideTheOverlay()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-portfolio-item").First.ClickAsync();
        await page.Locator(".gws-portfolio-lightbox-close").ClickAsync();

        (await page.Locator(".gws-portfolio-lightbox").IsVisibleAsync()).Should().BeFalse();
    }
}
