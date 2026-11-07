using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Sentinel's page tree in its sidebar, with the real app.css (07-11-26: on Grant's iPad the sidebar
// panned sideways, because rows with four 44px touch buttons were wider than it). The sidebar
// scrolls up and down only, and every row fits, its ⋯ menu included.
[Collection("Playwright")]
public sealed class SentinelSidebarLayoutBrowserTests(PlaywrightBrowserFixture fixture)
{
    // The app's own stylesheets, in the order App.razor loads them (the touch sizes are in the second).
    private static string AppCss => string.Concat(new[] { "app.css", "css/gws-responsive.css" }.Select(file =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot", file)))));

    // The rows as Wiki.razor draws them: a checkbox, the expand toggle, the title, then favorite,
    // move up, move down and the ⋯ menu. Deep, long-titled pages are the hard case.
    private static string Sidebar()
    {
        var rows = string.Concat(Enumerable.Range(0, 8).Select(depth => $"""
            <div class="wiki-tree-row" style="--tree-depth:{depth};">
              <input type="checkbox" class="form-check-input wiki-tree-select" />
              <button type="button" class="wiki-tree-toggle"><i class="bi bi-caret-right-fill"></i></button>
              <button type="button" class="wiki-tree-label"><span class="wiki-tree-icon">📄</span><span class="wiki-tree-title">A page with a rather long title at depth {depth}</span></button>
              <div class="wiki-tree-actions">
                <button type="button" class="btn btn-xs btn-link p-0" title="Add to favorites">☆</button>
                <button type="button" class="btn btn-xs btn-link p-0" title="Move up">↑</button>
                <button type="button" class="btn btn-xs btn-link p-0" title="Move down">↓</button>
                <div class="wiki-tree-row-menu"><button type="button" class="btn btn-xs btn-link p-0 more" title="More actions">⋯</button>
                  <div class="dropdown-menu show wiki-tree-row-dropdown">
                    <button type="button" class="dropdown-item wiki-tree-touch-only">Add to favorites</button>
                    <button type="button" class="dropdown-item">Rename</button>
                  </div>
                </div>
              </div>
            </div>
            """));
        return $"""
            <main class="gws-content">
              <div class="sentinel-workspace">
                <aside class="sentinel-local-sidebar" style="width:254px;height:600px;position:static">
                  <div class="sentinel-nav-panel"><div class="sentinel-tree-scroll">{rows}</div></div>
                </aside>
              </div>
            </main>
            """;
    }

    [Fact]
    public async Task OnATouchScreen_TheSidebarShouldNotPanSideways_AndEveryRowsMenuShouldFit()
    {
        await using var context = await fixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1180, Height = 820 }, HasTouch = true, IsMobile = true
        });
        var page = await context.NewPageAsync();
        await page.SetContentAsync($"<html><body>{Sidebar()}</body></html>");
        await page.AddStyleTagAsync(new() { Content = AppCss });

        (await page.EvaluateAsync<bool>("matchMedia('(pointer: coarse)').matches")).Should().BeTrue("the test needs the touch rules");
        foreach (var box in new[] { ".sentinel-local-sidebar", ".sentinel-tree-scroll" })
        {
            (await page.EvaluateAsync<int>($"() => {{ const el = document.querySelector('{box}'); el.scrollLeft = 300; return el.scrollLeft; }}"))
                .Should().Be(0, $"{box} scrolls up and down only");
        }

        var overflow = await page.EvaluateAsync<int[]>("""
            () => { const edge = document.querySelector('.sentinel-local-sidebar').getBoundingClientRect().right;
                    return [...document.querySelectorAll('.wiki-tree-row .more')].map(b => Math.round(b.getBoundingClientRect().right - edge)); }
            """);
        overflow.Should().HaveCount(8).And.OnlyContain(px => px <= 0, "every row's ⋯ is inside the sidebar, eight levels deep too");
        (await page.EvaluateAsync<int>("() => { const t = document.querySelector('.sentinel-tree-scroll'); return t.scrollWidth - t.clientWidth; }"))
            .Should().Be(0, "the rows fit, so there's nothing to the side");

        (await page.Locator(".wiki-tree-actions > .btn").First.IsVisibleAsync()).Should().BeFalse("touch rows keep only the ⋯ menu");
        (await page.Locator(".more").First.BoundingBoxAsync())!.Height.Should().BeGreaterThanOrEqualTo(44, "a touch target");
        (await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.wiki-tree-touch-only')).display")).Should().Be("block",
            "the menu has Favorite and Move up/down instead");
        (await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.wiki-tree-row-dropdown')).position")).Should().Be("fixed",
            "a sheet the tree's scroll area can't cut off");
    }

    [Fact]
    public async Task WithAMouse_TheRowsShouldKeepTheirOwnButtons_AndTheMenuItsUsualItems()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        await page.SetContentAsync($"<html><body>{Sidebar()}</body></html>");
        await page.AddStyleTagAsync(new() { Content = AppCss });

        (await page.Locator(".wiki-tree-actions > .btn").First.IsVisibleAsync()).Should().BeTrue();
        (await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.wiki-tree-touch-only')).display")).Should().Be("none");
        (await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.wiki-tree-row-dropdown')).position")).Should().Be("absolute");
        (await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.sentinel-tree-scroll')).overflowX")).Should().Be("hidden");
    }
}
