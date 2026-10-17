using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Drives the real whiteboard (wwwroot/js/sentinel-canvas.js) in Chromium: on its own, and as a
// block inside the page editor (wiki-block-editor.js loads it on demand).
[Collection("Playwright")]
public sealed class SentinelCanvasBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    [Fact]
    public async Task Board_ShouldAddEditConnectMoveAndUndo_ReportingEachChange()
    {
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1200, Height = 800 } });
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await OpenHarnessAsync(page);
        await page.EvaluateAsync("""
            async () => {
              const module = await import('/js/sentinel-canvas.js');
              window.changes = [];
              window.board = module.mountCanvas(document.getElementById('board'), {
                doc: '', onChange: (doc, text) => window.changes.push({ doc: JSON.parse(doc), text })
              });
            }
            """);
        var board = page.Locator("#board");
        var box = (await board.BoundingBoxAsync())!;

        // Sticky note: pick the tool, click, type, leave.
        await page.ClickAsync("[data-tool=sticky]");
        await page.Mouse.ClickAsync(box.X + 300, box.Y + 300);
        await page.Keyboard.TypeAsync("Kickoff");
        await page.Keyboard.PressAsync("Escape");
        // A rectangle further right, written in the same way.
        await page.Keyboard.PressAsync("r");
        await page.Mouse.ClickAsync(box.X + 800, box.Y + 300);
        await page.Keyboard.TypeAsync("Ship");
        await page.Keyboard.PressAsync("Escape");
        // Arrow from the sticky to the rectangle.
        await page.Keyboard.PressAsync("a");
        await page.Mouse.MoveAsync(box.X + 300, box.Y + 300);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + 600, box.Y + 310, new() { Steps = 5 });
        await page.Mouse.MoveAsync(box.X + 800, box.Y + 300, new() { Steps = 5 });
        await page.Mouse.UpAsync();

        var doc = await LastDocAsync(page);
        var items = doc.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("type").GetString()).Should().Equal("sticky", "rect", "connector");
        var sticky = items[0];
        sticky.GetProperty("text").GetString().Should().Be("Kickoff");
        items[2].GetProperty("from").GetString().Should().Be(sticky.GetProperty("id").GetString());
        items[2].GetProperty("to").GetString().Should().Be(items[1].GetProperty("id").GetString());
        (await page.EvaluateAsync<string>("() => window.changes.at(-1).text.map(s => s.text).join('')")).Should().Be("Kickoff\nShip");

        // Move the sticky by dragging it: its arrow follows (still attached).
        await page.Keyboard.PressAsync("v");
        await page.Mouse.MoveAsync(box.X + 260, box.Y + 260);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + 260, box.Y + 460, new() { Steps = 5 });
        await page.Mouse.UpAsync();
        var moved = (await LastDocAsync(page)).GetProperty("items")[0];
        moved.GetProperty("y").GetDouble().Should().BeApproximately(sticky.GetProperty("y").GetDouble() + 200, 1);
        (await page.Locator(".sc-connector").CountAsync()).Should().Be(1);

        // Delete it, then undo brings it back.
        await page.Keyboard.PressAsync("Delete");
        (await LastDocAsync(page)).GetProperty("items").GetArrayLength().Should().Be(2);
        await page.Keyboard.PressAsync("Control+z");
        (await LastDocAsync(page)).GetProperty("items").GetArrayLength().Should().Be(3);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Board_ShouldAddPageCards_ShowLivePreviews_AndHideRestrictedPages()
    {
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1200, Height = 800 } });
        await OpenHarnessAsync(page);
        await page.EvaluateAsync("""
            async () => {
              const module = await import('/js/sentinel-canvas.js');
              window.changes = [];
              window.opened = [];
              const doc = { v: 1, view: { x: 0, y: 0, zoom: 1 }, items: [
                { id: 'gone', type: 'card', pageId: '00000000-0000-0000-0000-00000000000b', title: 'Old title', x: 700, y: 400, w: 260, h: 170 } ] };
              window.board = module.mountCanvas(document.getElementById('board'), {
                doc: JSON.stringify(doc),
                onChange: d => window.changes.push(JSON.parse(d)),
                searchPages: async q => [{ id: '00000000-0000-0000-0000-00000000000a', title: 'Roadmap', icon: '🗺' }],
                pagePreview: async id => id.endsWith('a')
                  ? { title: 'Roadmap (live)', icon: '🗺', lines: [{ kind: 'text', text: 'Q4 goals' }] }
                  : null,
                openPage: id => window.opened.push(id)
              });
            }
            """);

        await page.ClickAsync("[data-action=card]");
        await page.FillAsync(".sentinel-canvas-picker input", "road");
        await page.ClickAsync(".sentinel-canvas-picker-item");

        await Expect(page.Locator(".sc-card-title").Filter(new() { HasText = "Roadmap (live)" })).ToBeVisibleAsync();
        await Expect(page.Locator(".sc-card-line")).ToHaveTextAsync("Q4 goals");
        await Expect(page.Locator(".sc-card-title").Filter(new() { HasText = "No access" })).ToBeVisibleAsync();
        (await page.Locator(".sc-card-title").AllTextContentsAsync()).Should().NotContain(text => text.Contains("Old title"));
        var card = (await page.EvaluateAsync<JsonElement>("() => window.changes.at(-1)")).GetProperty("items")[1];
        card.GetProperty("pageId").GetString().Should().Be("00000000-0000-0000-0000-00000000000a");
        card.GetProperty("title").GetString().Should().Be("Roadmap");

        await page.Locator(".sc-card-open").ClickAsync();
        (await page.EvaluateAsync<string[]>("() => window.opened")).Should().Equal("00000000-0000-0000-0000-00000000000a");
    }

    [Fact]
    public async Task ReadOnlyBoard_ShouldDrawItems_WithoutTools()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await OpenHarnessAsync(page);
        await page.EvaluateAsync("""
            async () => {
              const module = await import('/js/sentinel-canvas.js');
              module.mountCanvas(document.getElementById('board'), { readOnly: true, doc: JSON.stringify({ v: 1, view: {}, items: [
                { id: 'a', type: 'sticky', x: 0, y: 0, w: 200, h: 160, text: '<img src=x onerror=alert(1)>' } ] }) });
            }
            """);

        await Expect(page.Locator(".sc-label-sticky")).ToHaveTextAsync("<img src=x onerror=alert(1)>");
        (await page.Locator("#board img").CountAsync()).Should().Be(0);
        (await page.Locator(".sentinel-canvas-toolbar").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EditorBlock_ShouldLoadTheBoard_AndSaveItsDocAndWords()
    {
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1200, Height = 900 } });
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await RouteStaticAsync(page);
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<!DOCTYPE html><html><body><div id="editor" class="wiki-block-editor"></div></body></html>"""
        }));
        var moduleSource = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose };";
        await page.RouteAsync("http://localhost/js/editor-under-test.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = moduleSource
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddStyleTagAsync(new() { Path = Path.Combine(WebRoot, "app.css") });
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/js/editor-under-test.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        await page.EvaluateAsync("""
            () => {
              window.saved = [];
              const dotNet = { invokeMethodAsync: (method, ...args) => {
                if (method === 'OnBlocksChanged') window.saved.push(args[0]);
                return Promise.resolve(null);
              } };
              const blocks = [{ id: '11111111-1111-1111-1111-111111111111', type: 'canvas', indentLevel: 0, richText: [],
                props: { doc: JSON.stringify({ v: 1, items: [], view: { x: 0, y: 0, zoom: 1 } }) } }];
              window.sentinelBlockEditor.initialize(document.getElementById('editor'), dotNet, JSON.stringify(blocks), 'canvas-test');
            }
            """);

        await Expect(page.Locator(".wiki-canvas-host .sentinel-canvas-toolbar")).ToBeVisibleAsync();
        var box = (await page.Locator(".wiki-canvas-host").BoundingBoxAsync())!;
        await page.ClickAsync("[data-tool=sticky]");
        await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        await page.Keyboard.TypeAsync("Hello board");
        await page.Keyboard.PressAsync("Escape");
        // Typing and Delete inside the board must not reach the page editor around it.
        await page.Keyboard.PressAsync("Backspace");

        var blocksJson = await page.EvaluateAsync<string>("() => window.sentinelBlockEditor.getBlocksJson(document.getElementById('editor'))");
        var block = JsonDocument.Parse(blocksJson).RootElement[0];
        block.GetProperty("type").GetString().Should().Be("canvas");
        block.GetProperty("props").GetProperty("doc").GetString().Should().NotContain("Hello board", "Backspace deleted the selected sticky");
        (await page.EvaluateAsync<int>("() => window.saved.length")).Should().BeGreaterThan(1);
        var earlier = JsonDocument.Parse(await page.EvaluateAsync<string>("() => window.saved.find(s => s.includes('Hello board'))")).RootElement[0];
        earlier.GetProperty("richText")[0].GetProperty("text").GetString().Should().Be("Hello board");
        (await page.Locator(".wiki-block").CountAsync()).Should().Be(1);
        errors.Should().BeEmpty();
    }

    private static async Task<JsonElement> LastDocAsync(IPage page) =>
        await page.EvaluateAsync<JsonElement>("() => window.changes.at(-1).doc");

    private async Task OpenHarnessAsync(IPage page)
    {
        await RouteStaticAsync(page);
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<!DOCTYPE html><html><head><link rel="stylesheet" href="/app.css"></head><body><div id="board" class="wiki-canvas-host" style="width:1100px;height:700px"></div></body></html>"""
        }));
        await page.GotoAsync("http://localhost/harness");
    }

    private static async Task RouteStaticAsync(IPage page)
    {
        await page.RouteAsync("http://localhost/js/sentinel-canvas.js**", async route =>
            await route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Path = Path.Combine(WebRoot, "js", "sentinel-canvas.js") }));
        await page.RouteAsync("http://localhost/app.css", async route =>
            await route.FulfillAsync(new() { Status = 200, ContentType = "text/css", Path = Path.Combine(WebRoot, "app.css") }));
    }
}
