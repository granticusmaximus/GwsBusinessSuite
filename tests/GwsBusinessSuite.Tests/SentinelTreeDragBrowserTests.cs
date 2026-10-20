using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class SentinelTreeDragBrowserTests(PlaywrightBrowserFixture fixture)
{
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
