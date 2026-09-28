using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Workstream C, Tier 2 (stats/counter block) - BuildStatsCounterRuntimeScript only starts
// counting once its element scrolls into view (IntersectionObserver), so proving that needs a
// real browser and a real scroll, not just a static-shell unit test.
[Collection("Playwright")]
public sealed class CmsStatsCounterBrowserTests(PlaywrightBrowserFixture fixture)
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
                    Columns = [new LayoutColumn { Widgets = [new LayoutWidget { WidgetType = "spacer", Props = new() { ["height"] = "1600" } }] }]
                },
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
                                    WidgetType = "stats",
                                    Props = new() { ["itemsJson"] = """[{"value":"500","suffix":"+","label":"Projects Delivered"}]""" }
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
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildStatsCounterRuntimeScript()}</body></html>
            """;
    }

    [Fact]
    public async Task Stats_ShouldStayAtZero_BeforeScrollingTheCounterIntoView()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml(css));
        await page.WaitForTimeoutAsync(100);

        (await page.Locator(".gws-stats-number").InnerTextAsync()).Should().Be("0");
    }

    [Fact]
    public async Task Stats_ShouldCountUpToTheTargetValue_OnceScrolledIntoView()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-stats-number").ScrollIntoViewIfNeededAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.gws-stats-number').textContent === '500'",
            new PageWaitForFunctionOptions { Timeout = 3000 });

        (await page.Locator(".gws-stats-number").InnerTextAsync()).Should().Be("500");
    }
}
