using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Phase 4 (reading-progress widget) - BuildReadingProgressRuntimeScript drives the fill bar's
// width off window.scrollY on every scroll/resize; needs a real browser (not a unit test) to
// prove the bar actually grows as the page scrolls.
[Collection("Playwright")]
public sealed class CmsReadingProgressBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static async Task<string> ReadCssAsync(string stylesheet)
    {
        var cssPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot", stylesheet));
        return await File.ReadAllTextAsync(cssPath);
    }

    private static string BuildHtml(string css, IReadOnlyDictionary<string, string>? props = null)
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
                                new LayoutWidget { WidgetType = "reading-progress", Props = new(props ?? new Dictionary<string, string>()) },
                                new LayoutWidget { WidgetType = "spacer", Props = new() { ["height"] = "4000" } }
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
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildReadingProgressRuntimeScript()}</body></html>
            """;
    }

    private static async Task<double> BarWidthPercentAsync(IPage page) =>
        await page.EvaluateAsync<double>("""
            () => {
              const fill = document.querySelector('.gws-reading-progress-bar');
              return parseFloat(fill.style.width || '0');
            }
            """);

    [Fact]
    public async Task ReadingProgress_ShouldStartAtZero_BeforeAnyScrolling()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml(css));
        await page.WaitForTimeoutAsync(50);

        (await BarWidthPercentAsync(page)).Should().Be(0);
    }

    [Fact]
    public async Task ReadingProgress_ShouldGrowAsThePageScrollsDown()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml(css));

        await page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight * 0.5)");
        await page.WaitForTimeoutAsync(150);
        var midway = await BarWidthPercentAsync(page);
        midway.Should().BeGreaterThan(10).And.BeLessThan(90);

        await page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");
        await page.WaitForTimeoutAsync(150);
        var atBottom = await BarWidthPercentAsync(page);
        atBottom.Should().BeGreaterThan(midway);
        atBottom.Should().BeApproximately(100, 1);
    }

    [Fact]
    public async Task ReadingProgress_ShouldRenderAtTheRequestedPositionAndColor()
    {
        var css = await ReadCssAsync("cms-public.css");
        var props = new Dictionary<string, string> { ["position"] = "bottom", ["color"] = "#ff0000", ["height"] = "8" };
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml(css, props));

        (await page.Locator(".gws-reading-progress-bottom").CountAsync()).Should().Be(1);
        var barColor = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.gws-reading-progress-bar')).backgroundColor");
        barColor.Should().Be("rgb(255, 0, 0)");
        var barHeight = await page.EvaluateAsync<double>(
            "() => document.querySelector('.gws-reading-progress').getBoundingClientRect().height");
        barHeight.Should().BeApproximately(8, 0.5);
    }
}
