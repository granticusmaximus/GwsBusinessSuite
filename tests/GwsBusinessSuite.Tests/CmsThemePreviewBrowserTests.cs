using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class CmsThemePreviewBrowserTests(PlaywrightBrowserFixture fixture)
{
    [Fact]
    public async Task ThemeChanges_KeepContent_AndUpdateComputedColorsAtBothWidths()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("https://**/*", route => route.AbortAsync());
        var stylesheet = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/public-site.css")));
        foreach (var width in new[] { 1280, 390 })
        {
            await page.SetViewportSizeAsync(width, 800);
            var colors = new List<string>();
            foreach (var preset in CmsThemePresets.All)
            {
                await page.SetContentAsync(PublicSiteHtmlRenderer.Layout("Preview", "", null, "<h1>My existing website</h1>", tokens: preset.Tokens).Replace("<link rel=\"stylesheet\" href=\"/public-site.css\" />", $"<style>{stylesheet}</style>"));
                (await page.Locator("h1").InnerTextAsync()).Should().Be("My existing website");
                var actual = await page.Locator("body").EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");
                var expected = await page.EvaluateAsync<string>("color => { const el = document.createElement('div'); el.style.backgroundColor = color; document.body.append(el); const result = getComputedStyle(el).backgroundColor; el.remove(); return result; }", preset.Tokens.Colors.First(c => c.Name == "Surface").Hex);
                actual.Should().Be(expected);
                colors.Add(actual);
            }
            colors.Distinct().Count().Should().BeGreaterThan(1);
        }
    }

    [Fact]
    public async Task Preview_PreventsLinkNavigationAndFormSubmission()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("<a href='https://example.com/'>Navigate</a><form action='https://example.com/'><button>Submit</button></form>");
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/cms-theme-preview.js"));
        await page.AddScriptTagAsync(new() { Content = await File.ReadAllTextAsync(scriptPath) });
        await page.GetByText("Navigate").ClickAsync();
        await page.GetByText("Submit", new() { Exact = true }).ClickAsync();
        page.Url.Should().Be("about:blank");
        (await page.Locator("form").EvaluateAsync<bool>("el => el.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))")).Should().BeFalse();
    }
}
