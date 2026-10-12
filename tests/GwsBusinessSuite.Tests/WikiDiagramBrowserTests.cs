using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 17, diagram blocks, drawn by the real vendored Mermaid (wwwroot/lib/mermaid)
// under a CSP with no 'unsafe-eval' - as strict as the Sentinel and public-share pages.
[Collection("Playwright")]
public sealed class WikiDiagramBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string StrictCsp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:";

    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private const string Flowchart = "graph TD\n  A[Start] --> B{OK?}\n  B -->|yes| C[Ship it]\n  B -->|no| D[Fix]";
    private const string Sequence = "sequenceDiagram\n  Alice->>Bob: Ready?\n  Bob-->>Alice: Shipping";

    private async Task<(IPage Page, List<string> Errors, List<string> Requests)> OpenAsync(string body)
    {
        var page = await fixture.Browser.NewPageAsync();
        var errors = new List<string>();
        var requests = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
        page.Request += (_, request) => requests.Add(request.Url);
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = StrictCsp },
            Body = $"""<!DOCTYPE html><html><head><script src="/js/wikiRichContentHydrate.js"></script></head><body style="background:#fff">{body}</body></html>"""
        }));
        await page.RouteAsync("http://localhost/js/wikiRichContentHydrate.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Path = Path.Combine(WebRoot, "js", "wikiRichContentHydrate.js")
        }));
        await page.RouteAsync("http://localhost/lib/mermaid/mermaid.min.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Path = Path.Combine(WebRoot, "lib", "mermaid", "mermaid.min.js")
        }));
        await page.GotoAsync("http://localhost/harness");
        return (page, errors, requests);
    }

    [Fact]
    public async Task ReadOnlyView_ShouldDrawFlowchartsAndSequenceDiagrams_UnderAStrictCsp()
    {
        var html = WikiBlockHtmlRenderer.RenderPage([Block(Flowchart), Block(Sequence), Block("graph TD\n  A -->")]);
        var (page, errors, requests) = await OpenAsync($"<main id=\"content\">{html}</main>");
        await using var pageScope = page;

        (requests.Any(url => url.Contains("mermaid.min.js"))).Should().BeFalse("the library loads only when a diagram is drawn");
        await page.EvaluateAsync("() => window.wikiRichContentHydrate('#content')");
        var targets = page.Locator(".wiki-mermaid-target");
        await Expect(targets.Nth(0).Locator("svg")).ToBeVisibleAsync(new() { Timeout = 20000 });
        await Expect(targets.Nth(1).Locator("svg")).ToBeVisibleAsync(new() { Timeout = 20000 });
        await Expect(targets.Nth(2).Locator(".wiki-mermaid-error")).ToContainTextAsync("Diagram error:", new() { Timeout = 20000 });

        (await targets.Nth(0).TextContentAsync()).Should().Contain("Ship it");
        (await targets.Nth(1).TextContentAsync()).Should().Contain("Alice").And.Contain("Shipping");
        requests.Count(url => url.Contains("mermaid.min.js")).Should().Be(1, "it's fetched once for the whole page");
        errors.Should().NotContain(e => e.Contains("Content Security Policy") || e.Contains("unsafe-eval"));
    }

    [Fact]
    public async Task Editor_SlashDiagram_ShouldDrawAsYouType_AndSaveTheSource()
    {
        var (page, errors, _) = await OpenAsync("""<div id="editor" class="wiki-block-editor sentinel-workspace"></div>""");
        await using var pageScope = page;
        var moduleSource = await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js"));
        moduleSource = moduleSource.Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose };";
        await page.RouteAsync("http://localhost/js/editor-under-test.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = moduleSource
        }));
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/js/editor-under-test.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        await page.EvaluateAsync("() => window.sentinelBlockEditor.initialize(document.querySelector('#editor'), { invokeMethodAsync: () => Promise.resolve([]) }, '[]')");

        var content = page.Locator(".wiki-block-content");
        await content.FillAsync("/diagram");
        await page.GetByRole(AriaRole.Listbox, new() { Name = "Insert a block" }).GetByRole(AriaRole.Option).First.ClickAsync();
        await Expect(page.Locator(".wiki-block")).ToHaveAttributeAsync("data-block-type", WikiBlockTypes.Diagram);

        content = page.Locator(".wiki-block-content");
        await content.ClickAsync();
        await content.PressSequentiallyAsync("graph LR");
        await content.PressAsync("Shift+Enter");
        await content.PressSequentiallyAsync("  Draft --> Review --> Publish");
        await Expect(page.Locator(".wiki-diagram-preview svg")).ToBeVisibleAsync(new() { Timeout = 20000 });
        (await page.Locator(".wiki-diagram-preview").TextContentAsync()).Should().Contain("Publish");

        var blocks = WikiBlockJson.ParseBlocks(await page.EvaluateAsync<string>("() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))"));
        var diagram = blocks.Should().ContainSingle().Subject;
        diagram.Type.Should().Be(WikiBlockTypes.Diagram);
        diagram.PlainText.Should().Be("graph LR\n  Draft --> Review --> Publish");
        diagram.PlainText.Should().NotContain("<svg", "only the source is saved, never the drawing");
        errors.Should().NotContain(e => e.Contains("Content Security Policy"));
    }

    private static WikiBlock Block(string source) =>
        new(Guid.NewGuid(), WikiBlockTypes.Diagram, 0, [new WikiRichTextSpan(source)], new Dictionary<string, string>());
}
