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
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = """<div id="editor" class="wiki-block-editor"></div>"""
        }));
        await page.GotoAsync("http://localhost/editor");
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

    [Fact]
    public async Task NestedBlockActions_ShouldPreserveEntireBranches()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = """<div id="editor" class="wiki-block-editor"></div>"""
        }));
        await page.GotoAsync("http://localhost/editor");
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
            TextBlock("A", 0),
            TextBlock("A child", 1),
            TextBlock("A grandchild", 2),
            TextBlock("B", 0),
            TextBlock("B child", 1),
            TextBlock("C", 0)
        ]);
        await page.EvaluateAsync(
            """
            json => window.sentinelBlockEditor.initialize(
                document.querySelector('#editor'),
                { invokeMethodAsync: () => Promise.resolve([]) },
                json)
            """,
            initialJson);

        var blocks = page.Locator(".wiki-block");

        // Splitting a parent inserts its new sibling after the existing descendants.
        await page.EvaluateAsync(
            """
            () => {
                const content = document.querySelector('.wiki-block-content');
                content.focus();
                const range = document.createRange();
                range.selectNodeContents(content);
                range.collapse(false);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                content.dispatchEvent(new KeyboardEvent(
                    'keydown',
                    { key: 'Enter', bubbles: true, cancelable: true }));
            }
            """);
        pageErrors.Should().BeEmpty();
        var split = await EditorBlocksAsync(page);
        split.Select(block => (block.PlainText, block.IndentLevel)).Should().Equal(
            ("A", 0), ("A child", 1), ("A grandchild", 2), (string.Empty, 0),
            ("B", 0), ("B child", 1), ("C", 0));
        await page.Keyboard.PressAsync("Alt+Shift+Backspace");

        // Indent and outdent apply the same delta to the selected block's descendants.
        await OpenBlockActionAsync(blocks.Nth(3), page, "Indent");
        var indented = await EditorBlocksAsync(page);
        indented.Single(block => block.PlainText == "B").IndentLevel.Should().Be(1);
        indented.Single(block => block.PlainText == "B child").IndentLevel.Should().Be(2);
        await OpenBlockActionAsync(blocks.Nth(3), page, "Outdent");

        // Duplicate creates a complete branch with fresh IDs.
        await OpenBlockActionAsync(blocks.Nth(0), page, "Duplicate");
        var duplicated = await EditorBlocksAsync(page);
        duplicated.Select(block => block.PlainText).Should().Equal(
            "A", "A child", "A grandchild",
            "A", "A child", "A grandchild",
            "B", "B child", "C");
        duplicated.Select(block => block.Id).Should().OnlyHaveUniqueItems();

        // Moving a peer carries its descendants across the preceding branch.
        await blocks.Nth(6).Locator(".wiki-block-content").FocusAsync();
        await page.Keyboard.PressAsync("Alt+ArrowUp");
        var moved = await EditorBlocksAsync(page);
        moved.Select(block => (block.PlainText, block.IndentLevel)).Should().Equal(
            ("A", 0), ("A child", 1), ("A grandchild", 2),
            ("B", 0), ("B child", 1),
            ("A", 0), ("A child", 1), ("A grandchild", 2),
            ("C", 0));

        // One undo restores the whole move, and deleting a parent removes its whole branch.
        await page.Keyboard.PressAsync("Control+z");
        (await EditorBlocksAsync(page)).Select(block => block.PlainText).Should().Equal(
            "A", "A child", "A grandchild",
            "A", "A child", "A grandchild",
            "B", "B child", "C");
        await OpenBlockActionAsync(blocks.Nth(0), page, "Delete");
        var deleted = await EditorBlocksAsync(page);
        deleted.Select(block => (block.PlainText, block.IndentLevel)).Should().Equal(
            ("A", 0), ("A child", 1), ("A grandchild", 2),
            ("B", 0), ("B child", 1), ("C", 0));

        // Pointer reorder also resolves a descendant drop target to its peer-root branch.
        var handleBox = await blocks.Nth(0).Locator(".wiki-block-handle").BoundingBoxAsync();
        var targetBox = await blocks.Nth(4).BoundingBoxAsync();
        handleBox.Should().NotBeNull();
        targetBox.Should().NotBeNull();
        await page.Mouse.MoveAsync(handleBox!.X + (handleBox.Width / 2), handleBox.Y + (handleBox.Height / 2));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(targetBox!.X + (targetBox.Width / 2), targetBox.Y + targetBox.Height - 1);
        await page.Mouse.UpAsync();
        (await EditorBlocksAsync(page)).Select(block => (block.PlainText, block.IndentLevel)).Should().Equal(
            ("B", 0), ("B child", 1),
            ("A", 0), ("A child", 1), ("A grandchild", 2),
            ("C", 0));

        await blocks.Nth(0).Locator(".wiki-block-add").ClickAsync();
        (await EditorBlocksAsync(page)).Select(block => (block.PlainText, block.IndentLevel)).Should().Equal(
            ("B", 0), ("B child", 1), (string.Empty, 0),
            ("A", 0), ("A child", 1), ("A grandchild", 2),
            ("C", 0));
        pageErrors.Should().BeEmpty();
    }

    private static WikiBlock TextBlock(string text, int indentLevel) =>
        new(
            Guid.NewGuid(),
            WikiBlockTypes.Paragraph,
            indentLevel,
            [new WikiRichTextSpan(text)],
            new Dictionary<string, string>());

    private static async Task<IReadOnlyList<WikiBlock>> EditorBlocksAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");
        return WikiBlockJson.ParseBlocks(json);
    }

    private static async Task OpenBlockActionAsync(ILocator block, IPage page, string action)
    {
        await block.GetByRole(AriaRole.Button, new() { Name = "Block actions" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = action }).ClickAsync();
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
