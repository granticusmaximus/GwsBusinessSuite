using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Workstream C, Tier 2 (tabs widget) - BuildTabsRuntimeScript's click handler needs a real
// browser to prove clicking a tab actually swaps which panel is visible, not just a static-shell
// unit test of the initial markup.
[Collection("Playwright")]
public sealed class CmsTabsBrowserTests(PlaywrightBrowserFixture fixture)
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
                                    WidgetType = "tabs",
                                    Props = new()
                                    {
                                        ["itemsJson"] = """[{"label":"Features","content":"Feature content goes here."},{"label":"Pricing","content":"Pricing content goes here."}]"""
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
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildTabsRuntimeScript()}</body></html>
            """;
    }

    [Fact]
    public async Task Tabs_ShouldShowOnlyTheFirstPanel_BeforeAnyClick()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        (await page.Locator(".gws-tabs-panel:visible").CountAsync()).Should().Be(1);
        (await page.Locator(".gws-tabs-panel:visible").InnerTextAsync()).Should().Contain("Feature content");
    }

    [Fact]
    public async Task Tabs_ClickingTheSecondTab_ShouldSwitchTheVisiblePanel()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-tabs-tab", new PageLocatorOptions { HasTextString = "Pricing" }).ClickAsync();

        (await page.Locator(".gws-tabs-panel:visible").CountAsync()).Should().Be(1);
        (await page.Locator(".gws-tabs-panel:visible").InnerTextAsync()).Should().Contain("Pricing content");
        (await page.Locator(".gws-tabs-tab", new PageLocatorOptions { HasTextString = "Pricing" }).GetAttributeAsync("aria-selected")).Should().Be("true");
        (await page.Locator(".gws-tabs-tab", new PageLocatorOptions { HasTextString = "Features" }).GetAttributeAsync("aria-selected")).Should().Be("false");
    }
}
