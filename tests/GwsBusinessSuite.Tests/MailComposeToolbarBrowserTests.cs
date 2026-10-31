using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Mail's compose toolbar (31-10-26): the Markdown goes around the selection or in front of each
// selected line, and every change is announced as "input" (what Blazor's @bind listens for).
[Collection("Playwright")]
public sealed class MailComposeToolbarBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly string Script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/mail-compose.js"));

    [Fact]
    public async Task Bold_ShouldWrapTheSelection_LeavingEdgeSpacesOut_AndAnEmptyOneGetsAPlaceholder()
    {
        await using var page = await OpenAsync("Thanks for the plan");

        await page.EvaluateAsync("() => { const t = document.getElementById('body'); t.setSelectionRange(11, 15); return window.mailCompose.apply(t, 'bold'); }");
        (await ValueAsync(page)).Should().Be("Thanks for **the** plan", "the selection took the space after 'the', as a double-click does");
        (await page.EvaluateAsync<int>("() => window.inputs")).Should().Be(1);

        await page.EvaluateAsync("() => { const t = document.getElementById('body'); t.setSelectionRange(0, 0); return window.mailCompose.apply(t, 'link'); }");
        (await ValueAsync(page)).Should().StartWith("[link text](https://)Thanks");
    }

    [Fact]
    public async Task Lists_ShouldPrefixEveryLineTheSelectionTouches()
    {
        await using var page = await OpenAsync("eggs\nmilk\nbread");

        await page.EvaluateAsync("() => { const t = document.getElementById('body'); t.setSelectionRange(2, 7); return window.mailCompose.apply(t, 'numbers'); }");
        (await ValueAsync(page)).Should().Be("1. eggs\n2. milk\nbread");
    }

    [Fact]
    public async Task ShortcutKeys_ShouldFormatToo()
    {
        await using var page = await OpenAsync("make this italic");
        await page.EvaluateAsync("() => { const t = document.getElementById('body'); window.mailCompose.listen(t); t.focus(); t.setSelectionRange(10, 16); }");

        await page.Keyboard.PressAsync("Control+i");

        (await ValueAsync(page)).Should().Be("make this _italic_");
    }

    private static Task<string> ValueAsync(IPage page) => page.EvaluateAsync<string>("() => document.getElementById('body').value");

    private async Task<IPage> OpenAsync(string text)
    {
        var page = await fixture.Browser.NewPageAsync();
        var source = await File.ReadAllTextAsync(Script);
        await page.RouteAsync("http://localhost/mail-compose.js", route => route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = source }));
        await page.RouteAsync("http://localhost/compose", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """
                <!DOCTYPE html><html><body><textarea id="body"></textarea>
                <script type="module">
                  import * as mailCompose from '/mail-compose.js';
                  window.inputs = 0;
                  document.getElementById('body').addEventListener('input', () => window.inputs++);
                  window.mailCompose = mailCompose;
                </script></body></html>
                """
        }));
        await page.GotoAsync("http://localhost/compose");
        await page.WaitForFunctionAsync("() => !!window.mailCompose");
        await page.EvaluateAsync("text => { document.getElementById('body').value = text; }", text);
        return page;
    }
}
