using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// "Lock editing" (wiki-block-editor.js setLocked, 19-10-26): a locked page reads normally but
// nothing typed, pressed or pasted changes it, and nothing is reported to the server for saving.
[Collection("Playwright")]
public sealed class SentinelEditLockBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private async Task<IPage> OpenEditorAsync(params string[] paragraphs)
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<div class="sentinel-workspace"><div id="editor" class="wiki-block-editor"></div></div>"""
        }));
        await page.GotoAsync("http://localhost/editor");
        var source = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, setLocked, setBlocks };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Content = source });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        var blocks = paragraphs.Select(text => new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>())).ToList();
        await page.EvaluateAsync("""
            json => {
                window.__changes = 0;
                const dotNet = { invokeMethodAsync: name => { if (name === 'OnBlocksChanged') window.__changes++; return Promise.resolve([]); } };
                window.sentinelBlockEditor.initialize(document.querySelector('#editor'), dotNet, json);
            }
            """, WikiBlockJson.Serialize(blocks));
        return page;
    }

    private static Task SetLockedAsync(IPage page, bool locked) =>
        page.EvaluateAsync("locked => window.sentinelBlockEditor.setLocked(document.querySelector('#editor'), locked)", locked);

    [Fact]
    public async Task LockedEditor_ShouldIgnoreTypingAndEnter_AndReportNothing_UntilUnlocked()
    {
        await using var page = await OpenEditorAsync("Ship on Friday.");
        var paragraph = page.Locator(".wiki-block-content").First;
        await SetLockedAsync(page, true);

        (await paragraph.GetAttributeAsync("contenteditable")).Should().Be("false");
        await paragraph.ClickAsync();
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.TypeAsync(" Typed");
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Backspace");
        await page.WaitForTimeoutAsync(400);

        (await paragraph.InnerTextAsync()).Should().Be("Ship on Friday.");
        (await page.Locator(".wiki-block-content").CountAsync()).Should().Be(1);
        (await page.EvaluateAsync<int>("() => window.__changes")).Should().Be(0);
        (await page.Locator("#editor").GetAttributeAsync("class")).Should().Contain("is-edit-locked");

        await SetLockedAsync(page, false);
        (await paragraph.GetAttributeAsync("contenteditable")).Should().Be("true");
        await paragraph.ClickAsync();
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.TypeAsync(" Typed");
        await page.WaitForTimeoutAsync(400);
        (await paragraph.InnerTextAsync()).Should().Be("Ship on Friday. Typed");
        (await page.EvaluateAsync<int>("() => window.__changes")).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LockedEditor_ShouldKeepBlocksRenderedLater_ReadOnly()
    {
        // A co-editor's update or a reload re-renders the blocks while the page stays locked.
        await using var page = await OpenEditorAsync("First.");
        await SetLockedAsync(page, true);
        var json = WikiBlockJson.Serialize([
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Replaced.")], new Dictionary<string, string>()),
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 1, [new WikiRichTextSpan("Added.")], new Dictionary<string, string>())
        ]);
        await page.EvaluateAsync("json => window.sentinelBlockEditor.setBlocks(document.querySelector('#editor'), json)", json);
        await page.WaitForTimeoutAsync(100);

        var editable = await page.EvaluateAsync<int>("() => document.querySelectorAll('#editor [contenteditable=\"true\"]').length");
        editable.Should().Be(0);
        (await page.Locator(".wiki-block-content").AllInnerTextsAsync()).Should().Equal("Replaced.", "Added.");
    }
}
