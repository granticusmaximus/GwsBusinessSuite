using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Drives the real wikiLinks.js in a browser (Sentinel item 14, hover previews). The page's
// .NET reference is stubbed: GetWikiPagePreview answers from window.__previews (null = the user
// can't view that page) and every call is recorded in window.__calls.
[Collection("Playwright")]
public sealed class WikiLinkPreviewBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string ScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/wikiLinks.js"));

    private const string Html = """
        <!DOCTYPE html>
        <html><head>
          <script src="/js/wikiLinks.js"></script>
          <style>body { margin: 0; padding: 40px; font: 16px sans-serif; } a { display: inline-block; padding: 4px; }</style>
        </head><body>
          <p><a id="open" href="wikilink:11111111-1111-1111-1111-111111111111">Plans</a></p>
          <p><a id="secret" href="wikilink:22222222-2222-2222-2222-222222222222">Secret</a></p>
          <p id="away" style="margin-top: 300px">elsewhere</p>
        </body></html>
        """;

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'" },
            Body = Html
        }));
        var script = await File.ReadAllTextAsync(ScriptPath);
        await page.RouteAsync("http://localhost/js/wikiLinks.js", route => route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = script }));
        await page.GotoAsync("http://localhost/harness");
        await page.EvaluateAsync("""
            () => {
                window.__calls = [];
                window.__previews = {
                    '11111111-1111-1111-1111-111111111111': {
                        id: '11111111-1111-1111-1111-111111111111', title: '<img src=x onerror="window.__xss=1">Plans', icon: '🗺️',
                        verified: true, more: 2, updated: '12-10-26 15:00',
                        lines: [
                            { kind: 'heading', text: 'Launch plan', indent: 0, checked: false },
                            { kind: 'number', text: 'Book venue', indent: 0, checked: false },
                            { kind: 'number', text: 'Send invites', indent: 0, checked: false },
                            { kind: 'todo', text: 'Confirm', indent: 1, checked: true }
                        ]
                    }
                };
                window.gwsWikiLinks.init({
                    invokeMethodAsync: (name, ...args) => {
                        window.__calls.push({ name, args });
                        return Promise.resolve(name === 'GetWikiPagePreview' ? (window.__previews[args[0]] || null) : null);
                    }
                });
            }
            """);
        return page;
    }

    [Fact]
    public async Task Hovering_ShouldShowAPlainTextPreview_AfterADelay_AndHideOnLeave()
    {
        await using var pageScope = await OpenAsync();
        var page = pageScope;

        await page.HoverAsync("#open");
        await page.WaitForTimeoutAsync(150);
        (await page.QuerySelectorAsync(".gws-wiki-preview")).Should().BeNull("it waits ~400 ms before showing");
        await page.WaitForSelectorAsync(".gws-wiki-preview", new() { Timeout = 3000 });

        (await page.TextContentAsync(".gws-wiki-preview-title")).Should().Be("<img src=x onerror=\"window.__xss=1\">Plans", "page text is shown as text, never parsed as HTML");
        (await page.EvaluateAsync<bool>("() => !!window.__xss")).Should().BeFalse();
        (await page.TextContentAsync(".gws-wiki-preview-verified")).Should().Be("Verified");
        var lines = await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('.gws-wiki-preview-line')].map(l => l.textContent)");
        lines.Should().Equal("Launch plan", "1.Book venue", "2.Send invites", "☑Confirm");
        (await page.TextContentAsync(".gws-wiki-preview-footer")).Should().Be("2 more blocks · Updated 12-10-26 15:00");
        (await page.GetAttributeAsync("#open", "aria-describedby")).Should().Be("gws-wiki-preview");

        // Moving into the popover keeps it open; moving away closes it.
        await page.HoverAsync(".gws-wiki-preview-body");
        await page.WaitForTimeoutAsync(400);
        (await page.QuerySelectorAsync(".gws-wiki-preview")).Should().NotBeNull();
        await page.HoverAsync("#away");
        await page.WaitForSelectorAsync(".gws-wiki-preview", new() { State = WaitForSelectorState.Detached, Timeout = 3000 });
    }

    [Fact]
    public async Task APageTheUserCannotView_ShouldShowNothing_AndAnswersAreCached()
    {
        await using var pageScope = await OpenAsync();
        var page = pageScope;

        await page.HoverAsync("#secret");
        await page.WaitForTimeoutAsync(900);
        (await page.QuerySelectorAsync(".gws-wiki-preview")).Should().BeNull();

        await page.HoverAsync("#open");
        await page.WaitForSelectorAsync(".gws-wiki-preview", new() { Timeout = 3000 });
        await page.HoverAsync("#away");
        await page.WaitForSelectorAsync(".gws-wiki-preview", new() { State = WaitForSelectorState.Detached, Timeout = 3000 });
        await page.HoverAsync("#open");
        await page.WaitForSelectorAsync(".gws-wiki-preview", new() { Timeout = 3000 });
        (await page.EvaluateAsync<int>("() => window.__calls.filter(c => c.name === 'GetWikiPagePreview' && c.args[0].startsWith('1111')).length")).Should().Be(1);

        // Escape closes it; clicking a link closes it and navigates.
        await page.Keyboard.PressAsync("Escape");
        (await page.QuerySelectorAsync(".gws-wiki-preview")).Should().BeNull();
        await page.ClickAsync("#open");
        (await page.EvaluateAsync<string>("() => window.__calls.at(-1).name")).Should().Be("NavigateToWikiPageId");
    }
}
