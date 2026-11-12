using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// App-wide UX pass (12-11-26): at iPad-portrait widths (768-1023px) the full sidebar took 272px
// of 820, squeezing every page. There the rail stays and a workspace's pages open over the
// content; laptops keep the full sidebar. The real app.css, gws-responsive.css and app.js run
// against a minimal copy of MainLayout's shell.
[Collection("Playwright")]
public sealed class ShellTabletNavigationBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly string WwwRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private const string Shell = """
        <!doctype html><html><head><base href="/" />
        <link rel="stylesheet" href="/app.css" /><link rel="stylesheet" href="/css/gws-responsive.css" />
        </head><body>
        <div class="gws-layout">
          <header class="gws-admin-bar"><div class="gws-admin-bar-start">
            <button type="button" class="gws-sidebar-toggle gws-sidebar-toggle-desktop">Menu</button></div></header>
          <div class="gws-layout-body">
            <aside class="gws-sidebar" id="gws-sidebar" aria-label="Admin navigation">
              <nav class="gws-rail"><button type="button" class="gws-rail-item" data-gws-workspace="intel">Intel</button></nav>
              <div class="gws-subnav"><div data-workspace="intel"><a class="gws-nav-link" href="#threat-intel">Threat Intelligence</a></div></div>
            </aside>
            <button type="button" class="gws-sidebar-backdrop" data-gws-sidebar-close aria-label="Close navigation"></button>
            <div class="gws-body"><main id="gws-main-content" class="gws-content"><h1>Page</h1><input type="search" id="page-search" /></main></div>
          </div>
        </div>
        <script src="/app.js"></script>
        </body></html>
        """;

    private async Task<IPage> OpenAsync(int width, int height)
    {
        var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        await page.RouteAsync("http://localhost/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            if (path is "app.css" or "css/gws-responsive.css" or "app.js")
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
        await page.GotoAsync("http://localhost/admin/crm");
        // Busy machines: wait for the stylesheets and app.js's first layout pass, not a fixed delay.
        await page.WaitForLoadStateAsync(LoadState.Load);
        await page.WaitForFunctionAsync("() => getComputedStyle(document.querySelector('.gws-body')).marginLeft !== ''");
        return page;
    }

    private static Task<double> ContentMarginAsync(IPage page) =>
        page.EvaluateAsync<double>("() => parseFloat(getComputedStyle(document.querySelector('.gws-body')).marginLeft)");

    [Fact]
    public async Task Tablet_ShouldKeepOnlyTheRail_AndOpenAWorkspacesPagesOverTheContent()
    {
        await using var page = await OpenAsync(820, 1180);

        await page.WaitForFunctionAsync("() => parseFloat(getComputedStyle(document.querySelector('.gws-body')).marginLeft) <= 64");
        await Expect(page.Locator(".gws-subnav")).ToBeHiddenAsync();

        await page.ClickAsync("[data-gws-workspace='intel']");
        await Expect(page.Locator(".gws-nav-link")).ToBeVisibleAsync();
        (await ContentMarginAsync(page)).Should().BeLessThanOrEqualTo(64, "they open over the content, which doesn't move");

        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".gws-subnav")).ToBeHiddenAsync();

        await page.ClickAsync("[data-gws-workspace='intel']");
        await Expect(page.Locator(".gws-nav-link")).ToBeVisibleAsync();
        await page.Mouse.ClickAsync(780, 600);
        await Expect(page.Locator(".gws-subnav")).ToBeHiddenAsync();

        await page.ClickAsync("[data-gws-workspace='intel']");
        await Expect(page.Locator(".gws-nav-link")).ToBeVisibleAsync();
        await page.ClickAsync(".gws-nav-link");
        await Expect(page.Locator(".gws-subnav")).ToBeHiddenAsync();
        (await page.EvaluateAsync<string?>("() => localStorage.getItem('gws-sidebar-collapsed')")).Should().BeNull("the desktop choice isn't overwritten");
    }

    // The Mac app's Edit > Find (Cmd+F) raises gws-find: the page's own search box takes focus.
    [Fact]
    public async Task MacFind_ShouldFocusThePagesSearchBox()
    {
        await using var page = await OpenAsync(1440, 900);
        await page.EvaluateAsync("() => window.dispatchEvent(new CustomEvent('gws-find'))");
        (await page.EvaluateAsync<string>("() => document.activeElement.id")).Should().Be("page-search");
    }

    [Fact]
    public async Task Laptop_ShouldKeepTheFullSidebar()
    {
        await using var page = await OpenAsync(1440, 900);

        await Expect(page.Locator(".gws-nav-link")).ToBeVisibleAsync();
        (await ContentMarginAsync(page)).Should().BeGreaterThan(200);
    }
}
