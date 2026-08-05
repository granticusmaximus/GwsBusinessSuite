using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class WikiBlockEditorBrowserTests(PlaywrightBrowserFixture fixture)
{
    [Fact]
    public async Task ImportedRichText_ShouldRejectExecutableLinkSchemes()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = """<main class="sentinel-workspace"><div id="editor" class="wiki-block-editor"></div></main>"""
        }));
        await page.GotoAsync("http://localhost/editor");

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
        var wikiLinksPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/wikiLinks.js"));
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Content = await File.ReadAllTextAsync(wikiLinksPath)
        });

        var linkedPageId = Guid.NewGuid();
        var blocksJson = WikiBlockJson.Serialize(
        [
            new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.Paragraph,
                0,
                [
                    new WikiRichTextSpan("Unsafe", Link: "javascript:alert(1)"),
                    new WikiRichTextSpan("Safe", Link: "https://example.com"),
                    new WikiRichTextSpan("Page", Link: $"wikilink:{linkedPageId}")
                ],
                new Dictionary<string, string>())
        ]);
        await page.EvaluateAsync(
            """
            json => window.sentinelBlockEditor.initialize(
                document.querySelector('#editor'),
                window.wikiDotNetRef = {
                    invokeMethodAsync: (...args) => {
                        window.wikiLinkCalls = [...(window.wikiLinkCalls || []), args];
                        return Promise.resolve([]);
                    }
                },
                json)
            """,
            blocksJson);
        await page.EvaluateAsync("() => window.gwsWikiLinks.init(window.wikiDotNetRef)");

        var contentHtml = await page.Locator(".wiki-block-content").InnerHTMLAsync();
        contentHtml.Should().Contain("<a");
        await Expect(page.Locator(".wiki-block-content a")).ToHaveCountAsync(2);
        await Expect(page.Locator(".wiki-block-content a").First).ToHaveAttributeAsync("href", "https://example.com");
        await page.Locator("""a[href^="wikilink:"]""").ClickAsync();
        await page.WaitForFunctionAsync("() => (window.wikiLinkCalls || []).length === 1");
        var linkCall = await page.EvaluateAsync<string>(
            "() => JSON.stringify(window.wikiLinkCalls[0])");
        linkCall.Should().Be($"[\"NavigateToWikiPageId\",\"{linkedPageId}\"]");
        await Expect(page.Locator(".wiki-block-content")).ToContainTextAsync("UnsafeSafePage");
    }

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
    public async Task LocalDraftAndSelectionComment_ShouldSurviveAFullEditorRestart()
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

        var blockId = Guid.NewGuid();
        var initialJson = WikiBlockJson.Serialize(
        [
            new WikiBlock(
                blockId,
                WikiBlockTypes.Paragraph,
                0,
                [new WikiRichTextSpan("Original text")],
                new Dictionary<string, string>())
        ]);
        await page.EvaluateAsync(
            """
            json => {
                window.editorInvocations = [];
                window.editorRef = {
                    invokeMethodAsync: (name, ...args) => {
                        window.editorInvocations.push({ name, args });
                        return Promise.resolve([]);
                    }
                };
                window.sentinelBlockEditor.initialize(
                    document.querySelector('#editor'), window.editorRef, json, 'durable-draft-test');
            }
            """,
            initialJson);

        await page.EvaluateAsync("() => window.dispatchEvent(new Event('offline'))");
        await Expect(page.Locator(".wiki-offline-banner")).ToContainTextAsync("edits are saved safely");
        await page.Locator(".wiki-block-content").FillAsync("Unsaved local draft");
        await page.WaitForTimeoutAsync(350);
        await page.EvaluateAsync("() => window.dispatchEvent(new Event('online'))");
        await Expect(page.Locator(".wiki-offline-banner")).ToHaveCountAsync(0);
        var draftJson = await page.EvaluateAsync<string>(
            "() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");

        await page.EvaluateAsync(
            """
            json => {
                const editor = document.querySelector('#editor');
                window.sentinelBlockEditor.dispose(editor);
                sessionStorage.clear();
                window.sentinelBlockEditor.initialize(
                    editor, window.editorRef, json, 'durable-draft-test');
            }
            """,
            initialJson);

        await Expect(page.Locator(".wiki-block-content")).ToHaveTextAsync("Unsaved local draft");
        (await page.EvaluateAsync<int>(
            "() => window.editorInvocations.filter(item => item.name === 'OnDraftRecovered').length"))
            .Should().Be(1);

        await SelectWordAsync(page, "local");
        await page.GetByRole(AriaRole.Button, new() { Name = "Comment on selection" }).ClickAsync();
        var selectionInvocation = await page.EvaluateAsync<JsonElement>(
            "() => window.editorInvocations.find(item => item.name === 'OpenSelectionDiscussion')");
        selectionInvocation.GetProperty("args")[0].GetString().Should().Be(blockId.ToString());
        selectionInvocation.GetProperty("args")[1].GetString().Should().Be("local");
        selectionInvocation.GetProperty("args")[2].GetInt32().Should().Be(8);
        selectionInvocation.GetProperty("args")[3].GetInt32().Should().Be(13);

        await page.EvaluateAsync(
            """
            json => {
                const editor = document.querySelector('#editor');
                window.sentinelBlockEditor.dispose(editor);
                window.sentinelBlockEditor.initialize(
                    editor, window.editorRef, json, 'durable-draft-test');
            }
            """,
            draftJson);
        (await page.EvaluateAsync<string?>(
            "() => localStorage.getItem('sentinel:block-draft:v1:durable-draft-test')"))
            .Should().BeNull();
    }

    [Fact]
    public async Task Columns_ShouldAddRemoveAndMoveWithoutLosingContent()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""<div id="editor" class="wiki-block-editor"></div>""");
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
                WikiBlockTypes.Columns,
                0,
                [new WikiRichTextSpan("First ||| Second")],
                new Dictionary<string, string>())
        ]);
        await page.EvaluateAsync(
            """
            json => window.sentinelBlockEditor.initialize(
                document.querySelector('#editor'),
                { invokeMethodAsync: () => Promise.resolve([]) },
                json)
            """,
            initialJson);

        var columns = page.Locator(".wiki-column-editor");
        await Expect(columns).ToHaveCountAsync(2);
        await columns.Nth(0).GetByRole(AriaRole.Button, new() { Name = "Move column right" }).ClickAsync();
        (await columns.Locator(".wiki-column-content").AllTextContentsAsync()).Should().Equal("Second", "First");

        await page.GetByRole(AriaRole.Button, new() { Name = "Add column" }).ClickAsync();
        await Expect(columns).ToHaveCountAsync(3);
        await columns.Nth(2).Locator(".wiki-column-content").FillAsync("Third");
        await columns.Nth(1).GetByRole(AriaRole.Button, new() { Name = "Remove column" }).ClickAsync();
        await Expect(columns).ToHaveCountAsync(2);

        var serialized = (await EditorBlocksAsync(page)).Single();
        serialized.PlainText.Should().Be("Second ||| Third");
    }

    [Fact]
    public async Task ImportedInlineBoard_ShouldRenderGroupedCardsAndRemainNavigable()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(
            """<main class="sentinel-workspace"><div id="editor" class="wiki-block-editor"></div></main>""");
        var stylesPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/app.css"));
        await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = await File.ReadAllTextAsync(stylesPath) });
        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/wiki-block-editor.js"));
        var moduleSource = await File.ReadAllTextAsync(scriptPath);
        moduleSource = moduleSource.Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Content = moduleSource });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");

        var databaseId = Guid.NewGuid();
        var titlePropertyId = Guid.NewGuid();
        var statusPropertyId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var blockJson = WikiBlockJson.Serialize(
        [
            new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.InlineDatabase,
                0,
                [],
                new Dictionary<string, string>
                {
                    ["databaseId"] = databaseId.ToString(),
                    ["databaseTitle"] = "Work Items"
                })
        ]);
        var snapshot = new WikiInlineDatabaseSnapshot(
            databaseId,
            "Work Items",
            "▦",
            [
                new WikiInlineDatabaseProperty(titlePropertyId, "Name", WikiDatabasePropertyTypes.Title, false, []),
                new WikiInlineDatabaseProperty(
                    statusPropertyId,
                    "Status",
                    WikiDatabasePropertyTypes.Select,
                    false,
                    [
                        new WikiDatabasePropertyOption("todo", "Not started", "gray"),
                        new WikiDatabasePropertyOption("doing", "In progress", "blue"),
                        new WikiDatabasePropertyOption("done", "Done", "green")
                    ])
            ],
            [
                new WikiInlineDatabaseRow(
                    rowId,
                    [
                        new WikiInlineDatabaseCell(titlePropertyId, "Review workflow"),
                        new WikiInlineDatabaseCell(statusPropertyId, "doing")
                    ])
            ])
        {
            Views =
            [
                new WikiInlineDatabaseView(Guid.NewGuid(), "Work Items", WikiDatabaseViewTypes.Board, null)
            ]
        };
        var payload = JsonSerializer.Serialize(
            new { blockJson, snapshot },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        await page.EvaluateAsync(
            """
            payloadJson => {
                const payload = JSON.parse(payloadJson);
                window.editorCalls = [];
                window.sentinelBlockEditor.initialize(
                    document.querySelector('#editor'),
                    {
                        invokeMethodAsync: (method, ...args) => {
                            window.editorCalls.push({ method, args });
                            return Promise.resolve(
                                ['GetInlineDatabase', 'MoveInlineDatabaseRow', 'AddInlineBoardTask'].includes(method)
                                    ? payload.snapshot
                                    : null);
                        }
                    },
                    payload.blockJson);
            }
            """,
            payload);

        await Expect(page.Locator(".wiki-inline-database-board")).ToBeVisibleAsync();
        await Expect(page.Locator(".wiki-inline-board-column")).ToHaveCountAsync(3);
        await Expect(page.Locator(".wiki-inline-board-status").Nth(1)).ToHaveAttributeAsync("data-color", "blue");
        await Expect(page.Locator(".wiki-inline-board-card")).ToHaveTextAsync("Review workflow");
        await page.Locator(".wiki-inline-board-card").ClickAsync();
        var openRowCall = await page.EvaluateAsync<string>(
            "() => JSON.stringify(window.editorCalls.at(-1))");
        openRowCall.Should().Contain("OpenLinkedDatabaseRow")
            .And.Contain(databaseId.ToString())
            .And.Contain(rowId.ToString());

        var columns = page.Locator(".wiki-inline-board-column");
        var emptyColumnBox = await columns.Nth(0).BoundingBoxAsync();
        var taskColumnBox = await columns.Nth(1).BoundingBoxAsync();
        emptyColumnBox.Should().NotBeNull();
        taskColumnBox.Should().NotBeNull();
        taskColumnBox!.Height.Should().BeGreaterThan(emptyColumnBox!.Height,
            "each board column should end beneath its own lowest task instead of stretching to the tallest column");

        await page.Locator(".wiki-inline-board-card")
            .DragToAsync(columns.Nth(2).Locator(".wiki-inline-board-cards"));
        await page.WaitForFunctionAsync(
            "() => window.editorCalls.some(call => call.method === 'MoveInlineDatabaseRow')");
        var moveCall = await page.EvaluateAsync<string>(
            "() => JSON.stringify(window.editorCalls.find(call => call.method === 'MoveInlineDatabaseRow'))");
        moveCall.Should().Contain(databaseId.ToString()).And.Contain(statusPropertyId.ToString()).And.Contain("done");

        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "New task" })).ToHaveCountAsync(3);
        await page.GetByRole(AriaRole.Button, new() { Name = "New task" }).Nth(1).ClickAsync();
        var taskName = page.GetByRole(AriaRole.Textbox, new() { Name = "New task in In progress" });
        await Expect(taskName).ToBeFocusedAsync();
        await taskName.FillAsync("Write launch notes");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add task" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => window.editorCalls.some(call => call.method === 'AddInlineBoardTask')");
        var addCall = await page.EvaluateAsync<string>(
            "() => JSON.stringify(window.editorCalls.find(call => call.method === 'AddInlineBoardTask'))");
        addCall.Should().Contain(databaseId.ToString())
            .And.Contain(statusPropertyId.ToString())
            .And.Contain("doing")
            .And.Contain("Write launch notes");
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

    [Fact]
    public async Task SuggestionMenus_ShouldSupportKeyboardSelectionAndIgnoreStaleResults()
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

        var pageId = Guid.NewGuid();
        var stalePageId = Guid.NewGuid();
        await page.EvaluateAsync(
            """
            ids => window.sentinelBlockEditor.initialize(
                document.querySelector('#editor'),
                {
                    invokeMethodAsync: (method, query) => {
                        if (method === 'SearchWikiLinkSuggestions') {
                            if (query === 'a') {
                                return new Promise(resolve => setTimeout(
                                    () => resolve([{ id: ids.stale, title: 'Stale page' }]), 100));
                            }
                            if (query === 'ab') {
                                return Promise.resolve([{ id: ids.page, title: 'Current page' }]);
                            }
                            return Promise.resolve([]);
                        }
                        if (method === 'SearchMentionSuggestions') {
                            return Promise.resolve([
                                { kind: 'user', value: 'Grant', label: '@Grant', description: 'Person' },
                                { kind: 'row', value: 'db:row', label: 'Grant project', description: 'Row in Projects' }
                            ]);
                        }
                        return Promise.resolve([]);
                    }
                },
                '[]')
            """,
            new { page = pageId, stale = stalePageId });

        var content = page.Locator(".wiki-block-content");
        await content.FillAsync("/heading");
        var slashMenu = page.GetByRole(AriaRole.Listbox, new() { Name = "Insert a block" });
        await Expect(slashMenu).ToBeVisibleAsync();
        await Expect(slashMenu.GetByRole(AriaRole.Option).Nth(0)).ToHaveAttributeAsync("aria-selected", "true");
        await content.PressAsync("ArrowDown");
        await Expect(slashMenu.GetByRole(AriaRole.Option).Nth(1)).ToHaveAttributeAsync("aria-selected", "true");
        await content.PressAsync("Enter");
        await Expect(page.Locator(".wiki-block")).ToHaveAttributeAsync("data-block-type", WikiBlockTypes.Heading2);

        content = page.Locator(".wiki-block-content");
        await content.FillAsync("[[a");
        await content.PressAsync("b");
        var linkMenu = page.GetByRole(AriaRole.Listbox, new() { Name = "Link to a Sentinel page" });
        await Expect(linkMenu.GetByRole(AriaRole.Option)).ToHaveCountAsync(1);
        await Expect(linkMenu.GetByRole(AriaRole.Option)).ToContainTextAsync("Current page");
        await page.WaitForTimeoutAsync(150);
        await Expect(linkMenu.GetByRole(AriaRole.Option)).ToContainTextAsync("Current page");
        await content.PressAsync("Enter");
        var linked = (await EditorBlocksAsync(page)).Single();
        linked.RichText.Should().ContainSingle(span =>
            span.Text == "Current page" && span.Link == $"wikilink:{pageId}");

        content = page.Locator(".wiki-block-content");
        await content.FillAsync("@gr");
        var mentionMenu = page.GetByRole(
            AriaRole.Listbox,
            new() { Name = "Mention a person, date, or database row" });
        await Expect(mentionMenu.GetByRole(AriaRole.Option)).ToHaveCountAsync(2);
        await Expect(mentionMenu).ToContainTextAsync("People");
        await Expect(mentionMenu).ToContainTextAsync("Database rows");
        await content.PressAsync("Tab");
        var mentioned = (await EditorBlocksAsync(page)).Single();
        mentioned.RichText.Should().ContainSingle(span =>
            span.Text == "@Grant" && span.Link == "usermention:Grant");
        await Expect(content).Not.ToHaveAttributeAsync("aria-expanded", "true");

        await content.FillAsync("/");
        await Expect(page.GetByRole(AriaRole.Listbox, new() { Name = "Insert a block" })).ToBeVisibleAsync();
        await content.PressAsync("Escape");
        await Expect(page.GetByRole(AriaRole.Listbox, new() { Name = "Insert a block" })).ToHaveCountAsync(0);
        await Expect(content).ToHaveTextAsync("/");
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
