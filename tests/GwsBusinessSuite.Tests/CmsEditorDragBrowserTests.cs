using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

[Collection("Playwright")]
public sealed class CmsEditorDragBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private async Task<IPage> OpenEditorAsync()
    {
        var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1200, Height = 800 } });
        var layout = new PageLayout
        {
            Sections = [new() { Id = "section", Columns = [new() { Id = "column", Widgets =
                [new() { Id = "existing", WidgetType = "heading", Props = new() { ["text"] = "Existing heading" } }] }, new() { Id = "empty-column" }] }]
        };
        var canvas = "<html><head><link rel='stylesheet' href='/cms-public.css'></head><body>"
            + CmsBlockHtmlRenderer.Render(layout, "site", "home", editMode: true)
            + CmsBlockHtmlRenderer.BuildEditModeScript() + "</body></html>";
        var parent = """
            <html><body>
            <button draggable="true" data-gws-palette-widget="paragraph"><span>Add paragraph</span></button>
            <button draggable="true" data-gws-global-block-id="global-one" data-gws-global-block-kind="widget">Reusable block</button>
            <button draggable="true" data-gws-existing-widget="existing">Existing layer</button>
            <iframe id="cms-builder-iframe" src="/canvas" style="display:block;width:850px;height:600px;margin:30px;"></iframe>
            <script src="/js/cms-builder-bridge.js"></script><script src="/harness.js"></script>
            </body></html>
            """;
        await page.RouteAsync("http://localhost/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            var body = path switch
            {
                "/editor" => parent,
                "/canvas" => canvas,
                "/harness.js" => "window.calls=[]; window.gwsCmsBuilderBridge.init({ invokeMethodAsync: (...args) => { window.calls.push(args); return Promise.resolve(); } });",
                _ => await File.ReadAllTextAsync(Path.Combine(WebRoot, path.TrimStart('/')))
            };
            await route.FulfillAsync(new()
            {
                ContentType = path.EndsWith(".js") ? "application/javascript" : path.EndsWith(".css") ? "text/css" : "text/html",
                Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'self'; script-src 'self' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline'; frame-src 'self'" },
                Body = body
            });
        });
        await page.GotoAsync("http://localhost/editor");
        await page.WaitForFunctionAsync("() => window.calls.some(c => c[0] === 'OnIframeReady')");
        return page;
    }

    [Theory]
    [InlineData("[data-gws-palette-widget]", "OnCanvasInsertWidgetAsync", "paragraph")]
    [InlineData("[data-gws-global-block-id]", "OnCanvasInsertGlobalAsync", "global-one")]
    public async Task NativeDrag_FromPaletteIntoCanvas_WorksUnderCsp(string source, string method, string id)
    {
        await using var page = await OpenEditorAsync();
        var target = page.FrameLocator("iframe").Locator("[data-gws-widget-id='existing']");
        await page.Locator(source).DragToAsync(target);
        await page.WaitForFunctionAsync("method => window.calls.some(c => c[0] === method)", method);
        // Give the cross-frame fallback time to run too: the same drop must not insert twice.
        await page.WaitForTimeoutAsync(100);
        var calls = await page.EvaluateAsync<string[][]>("method => window.calls.filter(c => c[0] === method).map(c => c.map(String))", method);
        calls.Should().HaveCount(1);
        calls[0].Take(5).Should().Equal(method, id, "section", "column", "existing");
    }

    [Fact]
    public async Task ExistingLayer_CanMoveIntoAnEmptyColumn()
    {
        await using var page = await OpenEditorAsync();
        await page.Locator("[data-gws-existing-widget]").DragToAsync(page.FrameLocator("iframe").Locator("[data-gws-column-id='empty-column']"));
        await page.WaitForFunctionAsync("() => window.calls.some(c => c[0] === 'OnCanvasDropAsync')");
        var call = await page.EvaluateAsync<string[]>("window.calls.find(c => c[0] === 'OnCanvasDropAsync').map(String)");
        call.Take(5).Should().Equal("OnCanvasDropAsync", "existing", "section", "empty-column", "");
    }

    [Fact]
    public async Task CanvasHandle_CanMoveAnExistingBlockIntoAnEmptyColumn()
    {
        await using var page = await OpenEditorAsync();
        var frame = page.FrameLocator("iframe");
        await frame.Locator("[data-gws-widget-id='existing']").HoverAsync();
        var source = (await frame.Locator("[data-gws-drag-handle-for='existing']").BoundingBoxAsync())!;
        var target = (await frame.Locator("[data-gws-column-id='empty-column']").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(source.X + source.Width / 2, source.Y + source.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(target.X + target.Width / 2, target.Y + target.Height / 2, new() { Steps = 10 });
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("() => window.calls.some(c => c[0] === 'OnCanvasDropAsync')");
        var call = await page.EvaluateAsync<string[]>("window.calls.find(c => c[0] === 'OnCanvasDropAsync').map(String)");
        call.Take(5).Should().Equal("OnCanvasDropAsync", "existing", "section", "empty-column", "");
    }

    [Fact]
    public async Task ReinitializingBridge_DoesNotDuplicateCallbacks()
    {
        await using var page = await OpenEditorAsync();
        await page.EvaluateAsync("gwsCmsBuilderBridge.init({invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); }})");
        await page.Locator("[data-gws-palette-widget]").DragToAsync(page.FrameLocator("iframe").Locator("[data-gws-widget-id='existing']"));
        await page.WaitForFunctionAsync("() => window.calls.some(c => c[0] === 'OnCanvasInsertWidgetAsync')");
        await page.WaitForTimeoutAsync(100);
        (await page.EvaluateAsync<int>("window.calls.filter(c => c[0] === 'OnCanvasInsertWidgetAsync').length")).Should().Be(1);
    }
}
