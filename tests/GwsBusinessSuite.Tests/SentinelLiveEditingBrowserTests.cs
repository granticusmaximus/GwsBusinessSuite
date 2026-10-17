using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Live co-editing (Sentinel item 9): the page editor taking in another person's merged save.
[Collection("Playwright")]
public sealed class SentinelLiveEditingBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private const string A = "aaaaaaaa-0000-0000-0000-000000000000";
    private const string B = "bbbbbbbb-0000-0000-0000-000000000000";
    private const string C = "cccccccc-0000-0000-0000-000000000000";

    private static string Block(string id, string text) =>
        $$$"""{"id":"{{{id}}}","type":"paragraph","indentLevel":0,"richText":[{"text":"{{{text}}}"}],"props":{}}""";

    [Fact]
    public async Task RemoteBlocks_ShouldReplaceOnlyChangedBlocks_KeepTheCaret_AndNotEchoAsAnEdit()
    {
        await using var page = await OpenEditorAsync($"[{Block(A, "one")},{Block(B, "two")}]");
        var first = page.Locator($".wiki-block[data-block-id='{A}'] .wiki-block-content");
        await first.ClickAsync();
        await page.EvaluateAsync("""
            () => {
              document.querySelector('.wiki-block').dataset.marker = 'kept';
              const content = document.querySelector('.wiki-block .wiki-block-content');
              const range = document.createRange();
              range.setStart(content.firstChild, 2);
              range.collapse(true);
              getSelection().removeAllRanges();
              getSelection().addRange(range);
              window.saved.length = 0;
            }
            """);

        var applied = await page.EvaluateAsync<bool>($$"""
            () => window.sentinelBlockEditor.applyRemoteBlocks(document.getElementById('editor'),
              '[{{Block(A, "one")}},{{Block(B, "two - edited there")}},{{Block(C, "three")}}]', ['{{B}}', '{{C}}'], true)
            """);

        applied.Should().BeTrue();
        (await page.EvaluateAsync<string>("() => document.querySelector('.wiki-block').dataset.marker")).Should().Be("kept");
        (await page.Locator(".wiki-block .wiki-block-content").AllTextContentsAsync()).Should().Equal("one", "two - edited there", "three");
        (await page.EvaluateAsync<int>("() => window.saved.length")).Should().Be(0);
        await page.Keyboard.TypeAsync("X");
        (await first.TextContentAsync()).Should().Be("onXe");
    }

    [Fact]
    public async Task RemoteChangeToTheFocusedBlock_ShouldKeepTheCaretOffset_AndRemovedBlocksGo()
    {
        await using var page = await OpenEditorAsync($"[{Block(A, "hello")},{Block(B, "bye")}]");
        await page.Locator($".wiki-block[data-block-id='{A}'] .wiki-block-content").ClickAsync();
        await page.EvaluateAsync("""
            () => {
              const content = document.querySelector('.wiki-block .wiki-block-content');
              const range = document.createRange();
              range.setStart(content.firstChild, 5);
              range.collapse(true);
              getSelection().removeAllRanges();
              getSelection().addRange(range);
            }
            """);

        await page.EvaluateAsync($$"""
            () => window.sentinelBlockEditor.applyRemoteBlocks(document.getElementById('editor'),
              '[{{Block(A, "hello world")}}]', ['{{A}}'], true)
            """);
        await page.Keyboard.TypeAsync("!");

        (await page.Locator(".wiki-block .wiki-block-content").AllTextContentsAsync()).Should().Equal("hello! world");
        var blocks = JsonDocument.Parse(await page.EvaluateAsync<string>("() => window.sentinelBlockEditor.getBlocksJson(document.getElementById('editor'))")).RootElement;
        blocks.GetArrayLength().Should().Be(1);
    }

    private async Task<IPage> OpenEditorAsync(string blocksJson)
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<!DOCTYPE html><html><body><div id="editor" class="wiki-block-editor"></div></body></html>"""
        }));
        var moduleSource = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, applyRemoteBlocks };";
        await page.RouteAsync("http://localhost/js/editor-under-test.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = moduleSource
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/js/editor-under-test.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        await page.EvaluateAsync("""
            json => {
              window.saved = [];
              const dotNet = { invokeMethodAsync: (method, ...args) => {
                if (method === 'OnBlocksChanged') window.saved.push(args[0]);
                return Promise.resolve(null);
              } };
              window.sentinelBlockEditor.initialize(document.getElementById('editor'), dotNet, json, 'live-test');
            }
            """, blocksJson);
        return page;
    }
}
