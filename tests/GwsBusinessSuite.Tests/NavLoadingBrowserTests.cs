using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// The Overwatch boot screen over the content while an admin page loads (30-10-26): a slow page
// gets it straight after the click, a quick one never flashes it, and it always goes away. The
// fake Blazor below does what blazor.web.js does: its click handler prevents the click and pushes
// the new address, and enhancedload fires once the page is swapped in.
[Collection("Playwright")]
public sealed class NavLoadingBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string ScriptPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/gws-nav-loading.js"));

    [Fact]
    public async Task ASlowPage_ShouldShowTheBootScreenOverTheContent_UntilItLoads()
    {
        await using var page = await OpenAsync();
        await ClickAsync(page, "world-news");
        await page.Clock.RunForAsync(100);
        (await StateAsync(page)).Should().Be("none", "a page that arrives quickly never flashes it");

        await page.Clock.RunForAsync(100);
        (await StateAsync(page)).Should().Be("shown");
        (await page.Locator(".gws-boot-overlay").InnerTextAsync()).Should().Contain("INITIALIZING TACTICAL UPLINK...").And.Contain("STANDBY");
        var box = await page.EvaluateAsync<double[]>("() => { const r = document.querySelector('.gws-boot-overlay').getBoundingClientRect(); return [r.left, r.width]; }");
        box.Should().Equal([200, 600], "it covers the content area, not the menu beside it");

        await LoadedAsync(page);
        (await StateAsync(page)).Should().Be("fading");
        await page.Clock.RunForAsync(700);
        (await StateAsync(page)).Should().Be("none");
    }

    [Fact]
    public async Task AQuickPage_ShouldNeverShowIt()
    {
        await using var page = await OpenAsync();
        await ClickAsync(page, "world-news");
        await page.Clock.RunForAsync(60);
        await LoadedAsync(page);
        await page.Clock.RunForAsync(2000);
        (await StateAsync(page)).Should().Be("none");
    }

    [Fact]
    public async Task AClickThePageTakesOver_AndLinksToTheSamePage_ShouldNotShowIt()
    {
        await using var page = await OpenAsync();
        await ClickAsync(page, "handled");
        await ClickAsync(page, "same-page");
        await page.Clock.RunForAsync(2000);
        (await StateAsync(page)).Should().Be("none", "nothing navigated, so there's nothing to wait for");
    }

    [Fact]
    public async Task ANavigationThatNeverLands_ShouldNotLeaveItUp()
    {
        await using var page = await OpenAsync();
        await ClickAsync(page, "world-news");
        await page.Clock.RunForAsync(1000);
        (await StateAsync(page)).Should().Be("shown");
        await page.Clock.RunForAsync(15_000);
        await page.Clock.RunForAsync(700);
        (await StateAsync(page)).Should().Be("none");
    }

    [Fact]
    public async Task BackToAnotherPage_ShouldShowIt_ButNotForASectionOnTheSamePage()
    {
        await using var page = await OpenAsync();
        await ClickAsync(page, "world-news");
        await LoadedAsync(page);
        await page.EvaluateAsync("() => { history.pushState(null, '', '/admin/world-news#top'); dispatchEvent(new PopStateEvent('popstate')); }");
        await page.Clock.RunForAsync(500);
        (await StateAsync(page)).Should().Be("none", "only the #section changed");

        await page.EvaluateAsync("() => { history.pushState(null, '', '/admin/crm'); dispatchEvent(new PopStateEvent('popstate')); }");
        await page.Clock.RunForAsync(500);
        (await StateAsync(page)).Should().Be("shown");
    }

    private static Task ClickAsync(IPage page, string id) =>
        page.EvaluateAsync("id => document.getElementById(id).click()", id).ContinueWith(_ => page.Clock.RunForAsync(1)).Unwrap();

    private static Task LoadedAsync(IPage page) => page.EvaluateAsync("() => window.__enhancedload()");

    private static Task<string> StateAsync(IPage page) => page.EvaluateAsync<string>("""
        () => {
            const overlay = document.querySelector('.gws-boot-overlay');
            return !overlay ? 'none' : overlay.classList.contains('gws-boot-hidden') ? 'fading' : 'shown';
        }
        """);

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 800, Height = 600 } });
        await page.Clock.InstallAsync();
        await page.RouteAsync("http://localhost/admin/start", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """
                <!DOCTYPE html><html><head><style>
                body { margin: 0; } nav { position: fixed; left: 0; top: 0; width: 200px; height: 100vh; }
                main.gws-content { margin-left: 200px; height: 2000px; }
                .gws-boot-overlay { position: fixed; } .gws-boot-hidden { opacity: 0; }
                </style></head><body>
                <nav><a id="world-news" href="/admin/world-news">World News</a></nav>
                <main class="gws-content">
                    <a id="handled" href="/admin/crm">Opens a panel instead</a>
                    <a id="same-page" href="/admin/start#top">Top</a>
                </main>
                <script>
                    window.Blazor = { addEventListener: (name, handler) => { if (name === 'enhancedload') window.__enhancedload = handler; } };
                    document.addEventListener('click', event => {
                        const link = event.target.closest('a');
                        if (!link) return;
                        event.preventDefault();
                        if (link.id === 'world-news') history.pushState(null, '', link.href);
                    });
                </script>
                </body></html>
                """
        }));
        var source = await File.ReadAllTextAsync(ScriptPath);
        await page.RouteAsync("http://localhost/gws-nav-loading.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = source
        }));
        await page.GotoAsync("http://localhost/admin/start");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Url = "http://localhost/gws-nav-loading.js" });
        return page;
    }
}
