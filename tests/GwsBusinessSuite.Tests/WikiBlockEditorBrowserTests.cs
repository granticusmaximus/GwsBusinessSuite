using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class WikiBlockEditorBrowserTests(PlaywrightBrowserFixture fixture)
{
    [Fact]
    public async Task ImportedNotionBlocks_ShouldRenderAndRoundTripWithoutLosingStructure()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""
            <main class="sentinel-workspace">
                <div id="editor" class="wiki-block-editor"></div>
            </main>
            """);

        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/wiki-block-editor.js"));
        var moduleSource = await File.ReadAllTextAsync(scriptPath);
        moduleSource = moduleSource.Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Type = "module",
            Content = moduleSource
        });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");

        var blocks = NotionMarkdownBlockParser.Parse("""
            1. First
            2. Second

            <aside>
            💡 A **formatted** callout
            </aside>

            <details>
            <summary>More details</summary>
            Hidden body
            </details>

            | Name | Status |
            | --- | --- |
            | Sentinel | **Active** |

            ```csharp
            Console.WriteLine("ready");
            ```
            """);
        var blocksJson = WikiBlockJson.Serialize(blocks);
        await page.EvaluateAsync(
            """
            json => window.sentinelBlockEditor.initialize(
                document.querySelector('#editor'),
                { invokeMethodAsync: () => Promise.resolve([]) },
                json)
            """,
            blocksJson);

        (await page.Locator(".wiki-list-marker").AllTextContentsAsync())
            .Should().Equal("1.", "2.");
        await Expect(page.Locator(".wiki-callout-icon")).ToHaveTextAsync("💡");
        await Expect(page.Locator(".wiki-block[data-block-type=callout] b")).ToHaveTextAsync("formatted");
        await Expect(page.Locator(".wiki-native-table")).ToBeVisibleAsync();
        await Expect(page.Locator(".wiki-native-table td b")).ToHaveTextAsync("Active");
        await Expect(page.Locator(".wiki-code-language")).ToHaveTextAsync("csharp");

        var hiddenToggleChild = page.Locator(".wiki-block.is-toggle-hidden");
        await Expect(hiddenToggleChild).ToHaveCountAsync(1);
        await page.Locator(".wiki-toggle-button").ClickAsync();
        await Expect(hiddenToggleChild).ToHaveCountAsync(0);

        var roundTripJson = await page.EvaluateAsync<string>(
            "() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");
        var roundTrip = WikiBlockJson.ParseBlocks(roundTripJson);
        roundTrip.Single(block => block.Type == WikiBlockTypes.Callout).Props["icon"].Should().Be("💡");
        roundTrip.Single(block => block.Type == WikiBlockTypes.Code).Props["language"].Should().Be("csharp");
        var table = roundTrip.Single(block => block.Type == WikiBlockTypes.Table);
        using var tableJson = JsonDocument.Parse(table.Props["tableJson"]);
        tableJson.RootElement[1][1][0].GetProperty("bold").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FormattingColorsAndUndo_ShouldRoundTripAndSurviveEditorReinitialization()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://sentinel.test/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = """<div id="editor" class="wiki-block-editor"></div>"""
        }));
        await page.GotoAsync("http://sentinel.test/editor");
        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/wiki-block-editor.js"));
        var moduleSource = await File.ReadAllTextAsync(scriptPath);
        moduleSource = moduleSource.Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Content = moduleSource });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");

        var initialJson = WikiBlockJson.Serialize(
        [
            new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.Paragraph,
                0,
                [new WikiRichTextSpan("Alpha Beta Gamma")],
                new Dictionary<string, string>())
        ]);
        await page.EvaluateAsync(
            """
            json => window.sentinelBlockEditor.initialize(
                document.querySelector('#editor'),
                { invokeMethodAsync: () => Promise.resolve([]) },
                json,
                'formatting-test')
            """,
            initialJson);

        await SelectWordAsync(page, "Alpha");
        await page.GetByRole(AriaRole.Button, new() { Name = "Text color" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Text color red" }).ClickAsync();
        await page.WaitForTimeoutAsync(350);

        await SelectWordAsync(page, "Beta");
        await page.GetByRole(AriaRole.Button, new() { Name = "Background color" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Background color yellow" }).ClickAsync();
        await page.WaitForTimeoutAsync(350);

        var formattedJson = await page.EvaluateAsync<string>(
            "() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");
        var formatted = WikiBlockJson.ParseBlocks(formattedJson).Single();
        formatted.RichText.Single(span => span.Text == "Alpha").TextColor.Should().Be("red");
        formatted.RichText.Single(span => span.Text == "Beta").BackgroundColor.Should().Be("yellow");

        await page.EvaluateAsync(
            """
            json => {
                const editor = document.querySelector('#editor');
                window.sentinelBlockEditor.dispose(editor);
                window.sentinelBlockEditor.initialize(
                    editor,
                    { invokeMethodAsync: () => Promise.resolve([]) },
                    json,
                    'formatting-test');
            }
            """,
            formattedJson);
        await page.Locator(".wiki-block-content").FocusAsync();
        await page.Keyboard.PressAsync("Control+z");

        var undoneJson = await page.EvaluateAsync<string>(
            "() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");
        var undone = WikiBlockJson.ParseBlocks(undoneJson).Single();
        undone.RichText.Should().Contain(span => span.Text == "Alpha" && span.TextColor == "red");
        undone.RichText.Should().NotContain(span => span.BackgroundColor == "yellow");

        await page.Keyboard.PressAsync("Control+Shift+z");
        var redoneJson = await page.EvaluateAsync<string>(
            "() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");
        WikiBlockJson.ParseBlocks(redoneJson).Single().RichText
            .Should().Contain(span => span.Text == "Beta" && span.BackgroundColor == "yellow");
    }

    private static async Task SelectWordAsync(IPage page, string word)
    {
        await page.EvaluateAsync(
            """
            word => {
                const content = document.querySelector('.wiki-block-content');
                const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
                let node;
                while ((node = walker.nextNode())) {
                    const offset = node.textContent.indexOf(word);
                    if (offset < 0) continue;
                    const range = document.createRange();
                    range.setStart(node, offset);
                    range.setEnd(node, offset + word.length);
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    content.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
                    return;
                }
                throw new Error(`Unable to select ${word}`);
            }
            """,
            word);
    }

    private static ILocatorAssertions Expect(ILocator locator) =>
        Assertions.Expect(locator);
}
