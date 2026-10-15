using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 24, the editor side of suggested edits: Suggest mode must never leave its edits
// behind as a local draft (they'd be "recovered" into the page and autosaved onto it), and
// blocks with pending changes get a margin mark.
[Collection("Playwright")]
public sealed class SentinelSuggestionBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private const string DraftKey = "sentinel:block-draft:v1:suggest-test";

    [Fact]
    public async Task SuggestMode_ShouldKeepNoDraft_AndMarksShouldFollowTheirBlocks()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<!DOCTYPE html><html><body><div id="editor" class="wiki-block-editor"></div></body></html>"""
        }));
        var moduleSource = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose, setSuggesting, setSuggestionMarks };";
        await page.RouteAsync("http://localhost/js/editor-under-test.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = moduleSource
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/js/editor-under-test.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var json = $$$"""[{"id":"{{{first}}}","type":"paragraph","indentLevel":0,"richText":[{"text":"Hello"}],"props":{}},{"id":"{{{second}}}","type":"paragraph","indentLevel":0,"richText":[{"text":"Second"}],"props":{}}]""";
        await page.EvaluateAsync("""
            json => {
                window.recovered = 0;
                window.editorRef = { invokeMethodAsync: name => { if (name === 'OnDraftRecovered') window.recovered++; return Promise.resolve([]); } };
                window.sentinelBlockEditor.initialize(document.querySelector('#editor'), window.editorRef, json, 'suggest-test');
            }
            """, json);

        // Normal editing keeps a local draft.
        var firstContent = page.Locator(".wiki-block-content").First;
        await firstContent.ClickAsync();
        await firstContent.PressAsync("End");
        await firstContent.PressSequentiallyAsync(" there");
        await page.WaitForFunctionAsync($"() => localStorage.getItem('{DraftKey}') !== null");

        // Suggest mode drops it and writes no new one.
        await page.EvaluateAsync("() => window.sentinelBlockEditor.setSuggesting(document.querySelector('#editor'), true)");
        (await page.EvaluateAsync<string?>($"() => localStorage.getItem('{DraftKey}')")).Should().BeNull();
        await firstContent.PressSequentiallyAsync(", suggested");
        await page.WaitForTimeoutAsync(600);
        (await page.EvaluateAsync<string?>($"() => localStorage.getItem('{DraftKey}')")).Should().BeNull();
        (await page.EvaluateAsync<string>("() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))"))
            .Should().Contain("Hello there, suggested", "the edits are still in the editor to be submitted");

        // Discard: the reload shows the page as stored, with nothing "recovered".
        await page.EvaluateAsync("""
            json => {
                const editor = document.querySelector('#editor');
                window.sentinelBlockEditor.setSuggesting(editor, false);
                window.sentinelBlockEditor.dispose(editor);
                window.sentinelBlockEditor.initialize(editor, window.editorRef, json, 'suggest-test');
            }
            """, json);
        await Expect(page.Locator(".wiki-block-content").First).ToHaveTextAsync("Hello");
        (await page.EvaluateAsync<int>("() => window.recovered")).Should().Be(0);

        // Margin marks: only the marked block, and cleared when the list is empty.
        await page.EvaluateAsync("id => window.sentinelBlockEditor.setSuggestionMarks(document.querySelector('#editor'), [id])", second.ToString().ToUpperInvariant());
        await Expect(page.Locator($".wiki-block[data-block-id='{second}']")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("has-suggestion"));
        await Expect(page.Locator($".wiki-block[data-block-id='{first}']")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("has-suggestion"));
        await page.EvaluateAsync("() => window.sentinelBlockEditor.setSuggestionMarks(document.querySelector('#editor'), [])");
        await Expect(page.Locator(".wiki-block.has-suggestion")).ToHaveCountAsync(0);
        errors.Should().BeEmpty();
    }
}
