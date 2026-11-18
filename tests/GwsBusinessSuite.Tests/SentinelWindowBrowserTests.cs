using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Opening Sentinel on its own (18-11-26): the dashboard button and the right-click menu on the
// nav's Sentinel links. The real app.css and js/sentinel-window.js run against a minimal shell.
[Collection("Playwright")]
public sealed class SentinelWindowBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly string WwwRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
    private const string PageId = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    private const string Shell = $$"""
        <!doctype html><html><head><base href="/" /><link rel="stylesheet" href="/app.css" /></head><body>
        <nav class="gws-nav">
          <div class="gws-rail">
            <button type="button" class="gws-rail-item" data-gws-workspace="knowledge">Knowledge</button>
            <button type="button" class="gws-rail-item" data-gws-workspace="intelligence">Intel</button>
            <a class="gws-rail-item gws-rail-shortcut" id="pinned" href="/admin/sentinel?page={{PageId}}&tab=x">Plans</a>
          </div>
          <a class="gws-nav-link" id="boards" href="admin/boards">Boards</a>
        </nav>
        <main><a id="open" href="/admin/sentinel/window" data-gws-sentinel-window>Open Sentinel in New Window</a></main>
        <script>
          window.__opened = [];
          window.open = (url, name, features) => { window.__opened.push({ url, name, features }); return { focus() {} }; };
        </script>
        <script src="/js/sentinel-window.js"></script>
        <script>window.gwsSentinelWindow.navigate = url => window.__opened.push({ url });</script>
        </body></html>
        """;

    private async Task<IPage> OpenAsync(bool macApp)
    {
        var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        if (macApp) await page.AddInitScriptAsync("window.__gwsMacCatalystWebView = true;");
        await page.RouteAsync("http://localhost/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            if (path is "app.css" or "js/sentinel-window.js")
            {
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    ContentType = path.EndsWith(".js", StringComparison.Ordinal) ? "application/javascript" : "text/css",
                    Body = await File.ReadAllTextAsync(Path.Combine(WwwRoot, path))
                });
                return;
            }
            await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = Shell });
        });
        await page.GotoAsync("http://localhost/admin");
        await page.WaitForLoadStateAsync(LoadState.Load);
        return page;
    }

    private static Task<string[]> OpenedAsync(IPage page) =>
        page.EvaluateAsync<string[]>("() => window.__opened.map(entry => entry.url)");

    [Fact]
    public async Task DashboardButton_OpensSentinelInAPopupWindow_InABrowser()
    {
        await using var page = await OpenAsync(macApp: false);

        await page.ClickAsync("#open");

        (await OpenedAsync(page)).Should().Equal("/admin/sentinel/window");
        page.Url.Should().Be("http://localhost/admin", "this window stays where it was");
    }

    [Fact]
    public async Task RightClickOnTheKnowledgeRailButton_OffersANewAppWindow_InTheMacApp()
    {
        await using var page = await OpenAsync(macApp: true);

        await page.ClickAsync("[data-gws-workspace='knowledge']", new() { Button = MouseButton.Right });
        var item = page.GetByRole(AriaRole.Menuitem, new() { Name = "Open Sentinel in New App Window" });
        await Expect(item).ToBeFocusedAsync();
        await item.ClickAsync();

        (await OpenedAsync(page)).Should().Equal("gws-native://sentinel-window");
        await Expect(page.Locator(".gws-context-menu")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RightClickOnAPinnedSentinelPage_OpensThatPage()
    {
        await using var page = await OpenAsync(macApp: true);

        await page.ClickAsync("#pinned", new() { Button = MouseButton.Right });
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Open Sentinel in New App Window" }).ClickAsync();

        (await OpenedAsync(page)).Should().Equal($"gws-native://sentinel-window?page={PageId}");
    }

    [Fact]
    public async Task RightClickElsewhere_KeepsTheNormalMenu_AndEscapeCloses()
    {
        await using var page = await OpenAsync(macApp: false);

        await page.ClickAsync("[data-gws-workspace='intelligence']", new() { Button = MouseButton.Right });
        await page.ClickAsync("#boards", new() { Button = MouseButton.Right });
        await Expect(page.Locator(".gws-context-menu")).ToHaveCountAsync(0);

        await page.ClickAsync("[data-gws-workspace='knowledge']", new() { Button = MouseButton.Right });
        await Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "Open Sentinel in New Window" })).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".gws-context-menu")).ToHaveCountAsync(0);
        (await page.EvaluateAsync<string>("() => document.activeElement.dataset.gwsWorkspace")).Should().Be("knowledge");
    }
}
