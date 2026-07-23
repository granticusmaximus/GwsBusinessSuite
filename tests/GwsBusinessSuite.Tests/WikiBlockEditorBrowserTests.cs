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
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson };";
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

    private static ILocatorAssertions Expect(ILocator locator) =>
        Assertions.Expect(locator);
}
