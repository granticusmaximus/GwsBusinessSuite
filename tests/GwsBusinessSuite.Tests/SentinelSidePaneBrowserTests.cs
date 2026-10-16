using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Sentinel item 16: Shift-click on a [[link]] asks the host to open the page to the side; a
// host without a side pane (the row page) still navigates as before.
[Collection("Playwright")]
public sealed class SentinelSidePaneBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    [Theory]
    [InlineData(false, true, "NavigateToWikiPageId")]
    [InlineData(true, true, "OpenWikiPageToTheSide")]
    [InlineData(true, false, "OpenWikiPageToTheSide,NavigateToWikiPageId")]
    public async Task ShiftClickOnALink_ShouldOpenToTheSide_WhereTheHostCan(bool shift, bool hostHasSidePane, string expectedCalls)
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<!DOCTYPE html><html><body><div id="editor" class="wiki-block-editor"></div></body></html>"""
        }));
        var moduleSource = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize };";
        await page.RouteAsync("http://localhost/js/editor-under-test.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = moduleSource
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/js/editor-under-test.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        var target = Guid.NewGuid();
        var json = $$$"""[{"id":"{{{Guid.NewGuid()}}}","type":"paragraph","indentLevel":0,"richText":[{"text":"See "},{"text":"Roadmap","link":"wikilink:{{{target}}}"}],"props":{}}]""";
        await page.EvaluateAsync("""
            ([json, hasSidePane]) => {
                window.calls = [];
                const ref = { invokeMethodAsync: (name, ...args) => {
                    if (name === 'OpenWikiPageToTheSide' || name === 'NavigateToWikiPageId') window.calls.push(name + ':' + args[0]);
                    if (name === 'OpenWikiPageToTheSide' && !hasSidePane) return Promise.reject(new Error('no such method'));
                    return Promise.resolve([]);
                } };
                window.sentinelBlockEditor.initialize(document.querySelector('#editor'), ref, json);
            }
            """, new object[] { json, hostHasSidePane });

        await page.Locator("a[href^='wikilink:']").ClickAsync(new() { Modifiers = shift ? [KeyboardModifier.Shift] : [] });
        await page.WaitForTimeoutAsync(300);

        var calls = await page.EvaluateAsync<string[]>("() => window.calls");
        calls.Distinct().Should().Equal(expectedCalls.Split(',').Select(name => $"{name}:{target}"));
    }
}
