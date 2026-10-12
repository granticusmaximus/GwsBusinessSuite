using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 19: live reading stats (wiki-block-editor.js attachReadingStats/readingStats) and
// the focus-mode shortcuts (sentinel-workspace.js), driven in a real browser.
[Collection("Playwright")]
public sealed class SentinelFocusAndStatsBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private async Task<IPage> OpenEditorAsync(IReadOnlyList<WikiBlock> blocks)
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<div class="sentinel-workspace"><div id="editor" class="wiki-block-editor"></div><div id="stats" data-wiki-reading-stats></div></div>"""
        }));
        await page.GotoAsync("http://localhost/editor");
        var source = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose, readingStats };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Content = source });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        await page.EvaluateAsync("json => window.sentinelBlockEditor.initialize(document.querySelector('#editor'), { invokeMethodAsync: () => Promise.resolve([]) }, json)",
            WikiBlockJson.Serialize(blocks));
        return page;
    }

    private static WikiBlock Block(string type, string text) =>
        new(Guid.NewGuid(), type, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());

    [Fact]
    public async Task ReadingStats_ShouldCountTheProse_AndUpdateLiveWithTheSelection()
    {
        await using var page = await OpenEditorAsync([
            Block(WikiBlockTypes.Heading1, "Launch plan"),
            Block(WikiBlockTypes.Paragraph, "Ship the beta on Friday."),
            Block(WikiBlockTypes.Code, "var notCounted = 1;")
        ]);
        var stats = page.Locator("#stats");

        await Expect(stats).ToHaveTextAsync("7 words · 36 characters · under 1 min read");

        var paragraph = page.Locator(".wiki-block-content").Nth(1);
        await paragraph.ClickAsync();
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.TypeAsync(" Then celebrate.");
        await Expect(stats).ToHaveTextAsync("9 words · 52 characters · under 1 min read");

        await paragraph.SelectTextAsync();
        await Expect(stats).ToContainTextAsync("selected: 7 words, 40 characters");
    }

    [Fact]
    public async Task ReadingStats_ShouldCountWordsAndCharactersAsPeopleDo()
    {
        await using var page = await OpenEditorAsync([]);

        async Task<(int Words, int Characters, int Minutes)> Count(string text)
        {
            var result = await page.EvaluateAsync<int[]>("t => { const s = window.sentinelBlockEditor.readingStats(t); return [s.words, s.characters, s.minutes]; }", text);
            return (result[0], result[1], result[2]);
        }

        (await Count("")).Should().Be((0, 0, 0));
        (await Count("Café  déjà vu 🚀")).Should().Be((3, 14, 1), "accents and emoji are one character each, runs of spaces count once");
        (await Count("東京は晴れです")).Words.Should().BeGreaterThan(1, "words without spaces are still found");
        (await Count(string.Join(' ', Enumerable.Repeat("word", 690)))).Minutes.Should().Be(3);
        await Expect(page.Locator("#stats")).ToHaveTextAsync("0 words · 0 characters · nothing to read yet");
    }

    [Fact]
    public async Task FocusShortcuts_ShouldToggle_AndEscapeShouldExitOnlyWhenNothingElseIsOpen()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """
                <button class="sentinel-focus-toggle" onclick="window.__toggles = (window.__toggles || 0) + 1">focus</button>
                <button class="sentinel-focus-exit" onclick="window.__exits = (window.__exits || 0) + 1">exit</button>
                <input id="field" />
                """
        }));
        await page.GotoAsync("http://localhost/workspace");
        var source = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "sentinel-workspace.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal) + "\nwindow.sentinelWorkspace = { initialize, dispose };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Content = source });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelWorkspace)");
        await page.EvaluateAsync("() => window.sentinelWorkspace.initialize(document.querySelector('#field'))");
        await page.FocusAsync("#field");

        await page.Keyboard.PressAsync("ControlOrMeta+Shift+Period");
        (await page.EvaluateAsync<int>("() => window.__toggles || 0")).Should().Be(1);

        await page.EvaluateAsync("() => { const menu = document.createElement('div'); menu.className = 'wiki-editor-suggestion-menu'; menu.id = 'menu'; document.body.appendChild(menu); }");
        await page.Keyboard.PressAsync("Escape");
        (await page.EvaluateAsync<int>("() => window.__exits || 0")).Should().Be(0, "Escape closes the open menu first");

        await page.EvaluateAsync("() => document.getElementById('menu').remove()");
        await page.Keyboard.PressAsync("Escape");
        (await page.EvaluateAsync<int>("() => window.__exits || 0")).Should().Be(1);
    }
}
