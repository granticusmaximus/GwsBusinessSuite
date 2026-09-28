using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Workstream C, Tier 3 (carousel / testimonial slider) - BuildCarouselRuntimeScript's click
// handlers need a real browser to prove next/prev/dot navigation actually swaps the active
// slide, not just a static-shell unit test of the initial (first-slide-active) markup.
[Collection("Playwright")]
public sealed class CmsCarouselBrowserTests(PlaywrightBrowserFixture fixture)
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
                                    WidgetType = "carousel",
                                    Props = new()
                                    {
                                        ["itemsJson"] = """[{"title":"First slide"},{"title":"Second slide"},{"title":"Third slide"}]"""
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
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildCarouselRuntimeScript()}</body></html>
            """;
    }

    [Fact]
    public async Task Carousel_ShouldShowOnlyTheFirstSlide_BeforeAnyClick()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        (await page.Locator(".gws-carousel-slide.is-active").InnerTextAsync()).Should().Be("First slide");
    }

    [Fact]
    public async Task Carousel_ClickingNext_ShouldAdvanceToTheSecondSlide()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-carousel-next").ClickAsync();

        (await page.Locator(".gws-carousel-slide.is-active").InnerTextAsync()).Should().Be("Second slide");
    }

    [Fact]
    public async Task Carousel_ClickingPreviousFromTheFirstSlide_ShouldWrapToTheLastSlide()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-carousel-prev").ClickAsync();

        (await page.Locator(".gws-carousel-slide.is-active").InnerTextAsync()).Should().Be("Third slide");
    }

    [Fact]
    public async Task Carousel_ClickingADot_ShouldJumpToThatSlide()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator("[data-gws-carousel-index='2']").ClickAsync();

        (await page.Locator(".gws-carousel-slide.is-active").InnerTextAsync()).Should().Be("Third slide");
        (await page.Locator("[data-gws-carousel-index='2']").GetAttributeAsync("class")).Should().Contain("is-active");
    }
}
