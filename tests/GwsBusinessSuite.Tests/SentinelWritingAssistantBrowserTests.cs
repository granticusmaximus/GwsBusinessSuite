using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class SentinelWritingAssistantBrowserTests(PlaywrightBrowserFixture fixture)
{
    [Theory]
    [InlineData("improve", "Improve writing", false)]
    [InlineData("continue", "Continue writing", true)]
    public async Task WritingAction_ShouldInsertPlainTextAndNotifySave(string key, string label, bool append)
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await InitializeAsync(page, 450);
        await SelectAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Writing assistant", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = label, Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => window.saved?.length > 0");
        var expected = (append ? "Original sentence. " : "") + "Rewritten <b>plain</b> text.";
        (await page.Locator(".wiki-block-content").TextContentAsync()).Should().Be(expected);
        (await page.Locator(".wiki-block-content b").CountAsync()).Should().Be(0);
        (await page.EvaluateAsync<string>("() => JSON.parse(window.saved).map(b => b.richText.map(s => s.text).join('')).join('')"))
            .Should().Be(expected);
        (await page.EvaluateAsync<string>("() => window.actionKey")).Should().Be(key);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(450)]
    public async Task WritingMenu_ShouldFitViewportAndAllowRetryAfterConnectionFailure(int top)
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await InitializeAsync(page, top);
        await SelectAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Writing assistant", Exact = true }).ClickAsync();
        var menu = page.GetByRole(AriaRole.Menu, new() { Name = "Writing assistant" });
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Continue writing", Exact = true }).WaitForAsync();
        var bounds = await menu.BoundingBoxAsync();
        bounds!.Y.Should().BeGreaterThanOrEqualTo(0);
        (bounds.Y + bounds.Height).Should().BeLessThanOrEqualTo(600);
        await page.EvaluateAsync("() => window.failNext = true");
        var improve = page.GetByRole(AriaRole.Menuitem, new() { Name = "Improve writing", Exact = true });
        await improve.ClickAsync();
        await page.GetByRole(AriaRole.Status).WaitForAsync();
        (await improve.IsEnabledAsync()).Should().BeTrue();
        (await page.Locator(".wiki-block-content").TextContentAsync()).Should().Be("Original sentence.");
        await improve.ClickAsync();
        await page.WaitForFunctionAsync("() => window.saved?.length > 0");
    }

    private static async Task SelectAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
                const content = document.querySelector('.wiki-block-content');
                content.focus();
                const range = document.createRange();
                range.selectNodeContents(content);
                window.getSelection().removeAllRanges();
                window.getSelection().addRange(range);
                content.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
            }
            """);
    }

    private static async Task InitializeAsync(IPage page, int top)
    {
        await page.SetViewportSizeAsync(900, 600);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
        var catalog = JsonSerializer.Serialize(SentinelWritingActions.All.Select(a => new { key = a.Key, label = a.Label, icon = a.Icon }));
        var bootstrap = $$"""
            import { initialize } from '/js/wiki-block-editor.js';
            const blocks = [{ id: 'test-block', type: 'paragraph', indentLevel: 0,
                richText: [{ text: 'Original sentence.' }], props: {} }];
            initialize(document.querySelector('#editor'), {
                invokeMethodAsync: async (method, ...args) => {
                    if (method === 'GetWritingActions') return {{catalog}};
                    if (method === 'RunWritingAction') {
                        if (window.failNext) { window.failNext = false; throw new Error('Disconnected'); }
                        window.actionKey = args[0];
                        return { succeeded: true, text: 'Rewritten <b>plain</b> text.' };
                    }
                    if (method === 'OnBlocksChanged') window.saved = args[0];
                    return null;
                }
            }, JSON.stringify(blocks));
            """;
        await page.RouteAsync("http://localhost/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            var (type, body) = path switch
            {
                "/js/wiki-block-editor.js" => ("text/javascript", await File.ReadAllTextAsync(Path.Combine(root, "js/wiki-block-editor.js"))),
                "/bootstrap.js" => ("text/javascript", bootstrap),
                "/app.css" => ("text/css", await File.ReadAllTextAsync(Path.Combine(root, "app.css"))),
                "/test.css" => ("text/css", $"#editor {{ position:absolute; top:{top}px; left:100px; width:600px; }}"),
                _ => ("text/html", "<link rel=\"stylesheet\" href=\"/app.css\"><link rel=\"stylesheet\" href=\"/test.css\"><main class=\"sentinel-workspace\"><div id=\"editor\" class=\"wiki-block-editor\"></div></main><script type=\"module\" src=\"/bootstrap.js\"></script>")
            };
            await route.FulfillAsync(new()
            {
                ContentType = type, Body = body,
                Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "script-src 'self' https://cdn.jsdelivr.net" }
            });
        });
        await page.GotoAsync("http://localhost/editor");
        await page.Locator(".wiki-block-content").WaitForAsync();
    }
}
