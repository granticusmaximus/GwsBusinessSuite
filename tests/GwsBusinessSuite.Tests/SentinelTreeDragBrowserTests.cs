using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class SentinelTreeDragBrowserTests(PlaywrightBrowserFixture fixture)
{
    [Fact]
    public async Task EmbeddedWebKitPointerDrag_ShouldReparentRecentPageOntoExpandedParent()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""
            <div class="sentinel-workspace">
              <div class="sentinel-nav-item is-draggable" data-sentinel-drag-id="11111111-1111-4111-8111-111111111111" data-sentinel-drag-database="false" draggable="true">Recent page</div>
              <div class="wiki-tree-row has-children" data-sentinel-drop-id="22222222-2222-4222-8222-222222222222"><button class="wiki-tree-label">Parent page</button></div>
            </div>
            """);
        await page.EvaluateAsync("""
            () => Object.defineProperty(navigator, 'userAgent', {
                configurable: true,
                value: 'Mozilla/5.0 (Macintosh) AppleWebKit/620.1 GWSBusinessSuite'
            })
            """);
        var webRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
        var source = await File.ReadAllTextAsync(Path.Combine(webRoot, "js", "sentinel-workspace.js"));
        await page.AddScriptTagAsync(new()
        {
            Type = "module",
            Content = source.Replace("export function ", "function ", StringComparison.Ordinal)
                + "\nwindow.workspace = { initialize, dispose };"
        });
        await page.EvaluateAsync("""
            () => {
                window.__treeCalls = [];
                window.workspace.initialize(null, {
                    invokeMethodAsync: (...args) => { window.__treeCalls.push(args); return Promise.resolve(); }
                });
            }
            """);

        await page.Locator(".sentinel-nav-item").DragToAsync(page.Locator(".wiki-tree-label"));
        await page.WaitForFunctionAsync("() => window.__treeCalls.length === 1");

        var call = await page.EvaluateAsync<string>("() => JSON.stringify(window.__treeCalls[0])");
        call.Should().Be("[\"ReparentTreeNodeFromPointer\",\"11111111-1111-4111-8111-111111111111\",false,\"22222222-2222-4222-8222-222222222222\"]");
    }

    [Fact]
    public async Task PageDrag_ShouldExposeExpandedParentRowAsTheDropTarget()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""
            <div class="sentinel-workspace is-tree-dragging">
              <div class="wiki-tree-row has-children" id="parent">
                <button class="wiki-tree-toggle">Expand</button>
                <button class="wiki-tree-label">Parent page</button>
                <div class="wiki-tree-actions"><button>More</button></div>
              </div>
            </div>
            """);

        var webRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
        await page.AddStyleTagAsync(new() { Content = await File.ReadAllTextAsync(Path.Combine(webRoot, "app.css")) });

        var hitId = await page.Locator(".wiki-tree-label").EvaluateAsync<string>("""
            label => {
                const box = label.getBoundingClientRect();
                return document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)?.id || '';
            }
            """);

        hitId.Should().Be("parent", "the row must receive dragover/drop even above its child buttons");
    }
}
