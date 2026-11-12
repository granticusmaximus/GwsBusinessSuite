using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// App-wide UX pass (12-11-26): js/overwatch-prefetch.js warms Cesium (4 MB) for a desktop browser,
// but not in the Mac app (MapKit draws Overwatch there) or on a phone (mobile data). The script's
// fetch is replaced by a recorder.
[Collection("Playwright")]
public sealed class OverwatchPrefetchBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly string Script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/overwatch-prefetch.js"));

    private async Task<IPage> OpenAsync(int width, bool macApp)
    {
        var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 800 } });
        await page.AddInitScriptAsync((macApp ? "window.__gwsMacCatalystWebView = true;" : string.Empty) + """
            window.__fetched = [];
            window.fetch = function (url) { window.__fetched.push(String(url)); return Promise.resolve(); };
            window.requestIdleCallback = undefined;
            """);
        await page.RouteAsync("http://localhost/**", async route =>
        {
            if (route.Request.Url.EndsWith("/prefetch.js", StringComparison.Ordinal))
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = await File.ReadAllTextAsync(Script) });
            else
                await route.FulfillAsync(new()
                {
                    Status = 200, ContentType = "text/html",
                    Body = """<!doctype html><base href="/"><nav><a href="admin/osint" id="ow">Overwatch</a></nav><script src="/prefetch.js"></script>"""
                });
        });
        await page.GotoAsync("http://localhost/admin/crm");
        return page;
    }

    private static Task<int> FetchedAsync(IPage page) => page.EvaluateAsync<int>("() => window.__fetched.length");

    [Fact]
    public async Task Desktop_ShouldWarmCesium_OnHoverAndAtIdle()
    {
        await using var page = await OpenAsync(1440, macApp: false);
        await page.HoverAsync("#ow");
        (await FetchedAsync(page)).Should().Be(1, "pointing at Overwatch starts the download");
        await page.WaitForTimeoutAsync(3300);
        (await FetchedAsync(page)).Should().Be(1, "once a session");
    }

    [Theory]
    [InlineData(1440, true)]
    [InlineData(390, false)]
    public async Task MacAppAndPhones_ShouldNotDownloadCesium(int width, bool macApp)
    {
        await using var page = await OpenAsync(width, macApp);
        await page.HoverAsync("#ow");
        await page.WaitForTimeoutAsync(3300);
        (await FetchedAsync(page)).Should().Be(0);
    }
}
