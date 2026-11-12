using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// World News design pass (12-11-26): js/world-news.js turns keys into clicks on the page's own
// controls and mirrors the page's state into the address. The rows here stand in for Blazor's:
// each click records which row was opened and marks it current, as the page does.
[Collection("Playwright")]
public sealed class WorldNewsScriptBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string Markup = """
        <main class="gws-content" style="width:1200px">
          <div class="world-news-page" id="root">
            <input class="world-news-search" type="search" />
            <button class="world-news-refresh" onclick="window.opened.push('refresh')">Refresh</button>
            <div class="world-news-list">
              <a class="world-news-event" href="#a" id="a">A</a>
              <a class="world-news-event" href="#b" id="b">B</a>
              <a class="world-news-event" href="#c" id="c">C</a>
            </div>
            <button class="world-news-back" onclick="window.opened.push('back')">Back</button>
          </div>
        </main>
        <script type="module">
          window.opened = [];
          document.querySelectorAll('.world-news-event').forEach(function (row) {
            row.addEventListener('click', function (event) {
              event.preventDefault();
              window.opened.push(row.id);
              document.querySelectorAll('.world-news-event').forEach(function (r) { r.setAttribute('aria-current', r === row ? 'true' : 'false'); });
            });
          });
        </script>
        """;

    private async Task<IPage> OpenAsync(bool macApp)
    {
        var page = await fixture.Browser.NewPageAsync();
        if (macApp) await page.AddInitScriptAsync("window.__gwsMacCatalystWebView = true;");
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = "<!doctype html><html><head><base href=\"/\" /></head><body>" + Markup + "</body></html>"
        }));
        await page.GotoAsync("http://localhost/admin/world-news");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
        var source = await File.ReadAllTextAsync(Path.Combine(root, "js/world-news.js"));
        await page.AddScriptTagAsync(new()
        {
            Type = "module",
            Content = source.Replace("export function ", "function ", StringComparison.Ordinal)
                .Replace("export async function ", "async function ", StringComparison.Ordinal)
                + "\nwindow.worldNews = { attach, setUrl, isWide }; window.keys = attach(document.getElementById('root'));"
        });
        await page.WaitForFunctionAsync("() => Boolean(window.keys)");
        return page;
    }

    private static Task<string[]> OpenedAsync(IPage page) => page.EvaluateAsync<string[]>("() => window.opened");

    [Fact]
    public async Task Keys_ShouldMoveThroughTheList_FocusSearch_AndGoBack()
    {
        await using var page = await OpenAsync(macApp: false);

        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("ArrowUp");
        (await OpenedAsync(page)).Should().Equal("a", "b", "a");

        await page.Keyboard.PressAsync("/");
        (await page.EvaluateAsync<string>("() => document.activeElement.className")).Should().Be("world-news-search");
        await page.Keyboard.PressAsync("ArrowDown");
        (await OpenedAsync(page)).Should().HaveCount(3, "arrows type in the search box, not move the list");
        await page.Keyboard.PressAsync("Escape");
        (await page.EvaluateAsync<string>("() => document.activeElement.tagName")).Should().Be("BODY", "Escape leaves the search box");

        await page.Keyboard.PressAsync("Escape");
        (await OpenedAsync(page)).Should().EndWith("back");

        await page.Keyboard.PressAsync("Meta+f");
        (await page.EvaluateAsync<string>("() => document.activeElement.tagName")).Should().Be("BODY", "browsers keep their own Cmd+F");

        await page.EvaluateAsync("() => window.keys.dispose()");
        await page.Keyboard.PressAsync("ArrowDown");
        (await OpenedAsync(page)).Should().EndWith("back", "dispose removes the listener");
    }

    [Fact]
    public async Task MacApp_ShouldMapCommandKeysToSearchRefreshAndBack()
    {
        await using var page = await OpenAsync(macApp: true);

        await page.Keyboard.PressAsync("Meta+f");
        (await page.EvaluateAsync<string>("() => document.activeElement.className")).Should().Be("world-news-search");
        await page.Keyboard.PressAsync("Escape");
        await page.Keyboard.PressAsync("Meta+r");
        await page.Keyboard.PressAsync("Meta+BracketLeft");
        (await OpenedAsync(page)).Should().Equal("refresh", "back");
    }

    [Fact]
    public async Task SetUrl_ShouldReplaceTheAddressWithoutANewHistoryEntry_AndIsWideShouldMeasureTheContentArea()
    {
        await using var page = await OpenAsync(macApp: false);
        var before = await page.EvaluateAsync<int>("() => history.length");

        await page.EvaluateAsync("() => window.worldNews.setUrl('admin/world-news?view=latest&event=abc')");

        page.Url.Should().Be("http://localhost/admin/world-news?view=latest&event=abc");
        (await page.EvaluateAsync<int>("() => history.length")).Should().Be(before);
        (await page.EvaluateAsync<bool>("() => window.worldNews.isWide(document.getElementById('root'), 900)")).Should().BeTrue();
        (await page.EvaluateAsync<bool>("() => window.worldNews.isWide(document.getElementById('root'), 1300)")).Should().BeFalse();
    }
}
