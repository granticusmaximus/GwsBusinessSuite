using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Workstream C, Tier 3 (pricing table monthly/yearly toggle) - BuildPricingTableRuntimeScript's
// click handler needs a real browser to prove clicking "Yearly" actually swaps the displayed
// price, not just a static-shell unit test of the initial (monthly) markup.
[Collection("Playwright")]
public sealed class CmsPricingTableBrowserTests(PlaywrightBrowserFixture fixture)
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
                                    WidgetType = "pricing-table",
                                    Props = new()
                                    {
                                        ["itemsJson"] = """[{"name":"Pro","monthlyPrice":"$29/mo","yearlyPrice":"$290/yr"}]"""
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
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildPricingTableRuntimeScript()}</body></html>
            """;
    }

    [Fact]
    public async Task PricingTable_ShouldShowTheMonthlyPrice_BeforeAnyClick()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        (await page.Locator("[data-gws-pricing-value]").InnerTextAsync()).Should().Be("$29/mo");
    }

    [Fact]
    public async Task PricingTable_ClickingYearly_ShouldSwapToTheYearlyPrice()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator("[data-gws-pricing-period='yearly']").ClickAsync();

        (await page.Locator("[data-gws-pricing-value]").InnerTextAsync()).Should().Be("$290/yr");
        (await page.Locator("[data-gws-pricing-period='yearly']").GetAttributeAsync("class")).Should().Contain("is-active");
    }
}
