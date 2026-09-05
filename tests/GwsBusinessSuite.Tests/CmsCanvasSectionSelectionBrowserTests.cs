using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Clicking a section in the live-preview canvas reported as "does nothing". Two real causes:
// highlight() only ever outlined widgets, so a section select produced no lasting visual change;
// and a section's widgets fill it edge to edge, so a click essentially always hit a widget and
// the section could not be selected at all. These drive the real edit-mode markup + script in a
// browser and assert the feedback a user actually sees.
[Collection("Playwright")]
public sealed class CmsCanvasSectionSelectionBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string SectionId = "sec-one";
    private const string WidgetId = "wid-one";

    private static string BuildEditModePage()
    {
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    Id = SectionId,
                    Label = "Hero section",
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Widgets =
                            [
                                new LayoutWidget
                                {
                                    Id = WidgetId,
                                    WidgetType = "heading",
                                    Props = { ["text"] = "Pricing" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var body = CmsBlockHtmlRenderer.Render(layout, "site", "page", editMode: true);
        return $"<!doctype html><html><body>{body}{CmsBlockHtmlRenderer.BuildEditModeScript()}</body></html>";
    }


    // The edit-mode behaviour ships as /js/cms-edit-mode.js (it cannot be inline - see
    // CmsEditModeScriptCspTests), so the stub has to serve the real file for these to exercise
    // anything. Reading the shipped asset rather than a copy also means these tests fail if that
    // file goes missing or its path changes.
    private static string EditModeScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/cms-edit-mode.js"));

    private static async Task ServeCanvasAsync(IPage page, string html)
    {
        var script = await File.ReadAllTextAsync(EditModeScriptPath);
        // Playwright matches handlers in REVERSE registration order, so the broad HTML catch-all
        // has to be registered first or it swallows the script request and returns HTML for it.
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = html
        }));
        await page.RouteAsync("**/js/cms-edit-mode.js", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/javascript",
            Body = script
        }));
    }

    private static async Task<IPage> OpenCanvasAsync(IBrowser browser)
    {
        var page = await browser.NewPageAsync();
        await ServeCanvasAsync(page, BuildEditModePage());
        await page.GotoAsync("http://localhost/canvas");
        await page.WaitForFunctionAsync("() => window.__gwsCanvasReady === true", null,
            new() { Timeout = 5000 });
        return page;
    }

    [Fact]
    public async Task ClickingASection_ShouldOutlineIt()
    {
        await using var page = await OpenCanvasAsync(fixture.Browser);

        await page.ClickAsync($"[data-gws-section-handle='{SectionId}']");

        var selected = await page.QuerySelectorAsync(".gws-section-selected");
        selected.Should().NotBeNull("clicking a section must leave a visible selected outline");
        var selectedId = await selected!.GetAttributeAsync("data-gws-section-id");
        selectedId.Should().Be(SectionId);
    }

    [Fact]
    public async Task ClickingASection_ShouldShowTheSectionToolbar()
    {
        await using var page = await OpenCanvasAsync(fixture.Browser);

        await page.ClickAsync($"[data-gws-section-handle='{SectionId}']");

        var toolbar = await page.QuerySelectorAsync(".gws-section-toolbar");
        toolbar.Should().NotBeNull("a selected section offers its actions on the canvas");
        var labels = await page.EvalOnSelectorAllAsync<string[]>(
            ".gws-section-toolbar button", "els => els.map(e => e.textContent)");
        labels.Should().Contain("+ Add block");
        labels.Should().Contain("Duplicate");
        labels.Should().Contain("Delete");
    }

    [Fact]
    public async Task ClickingTheToolbar_ShouldPostASectionCommand_NotReselectTheSection()
    {
        // The toolbar sits inside the section it controls, so without the guard in the click
        // handler its own buttons would re-trigger a section select underneath them.
        await using var page = await OpenCanvasAsync(fixture.Browser);
        await page.ClickAsync($"[data-gws-section-handle='{SectionId}']");

        await page.EvaluateAsync(
            "() => { window.__msgs = []; const post = window.parent.postMessage.bind(window.parent);" +
            " window.parent.postMessage = (m, o) => { window.__msgs.push(m); return post(m, o); }; }");
        await page.ClickAsync(".gws-section-toolbar button.is-primary");

        var commands = await page.EvaluateAsync<string[]>(
            "() => window.__msgs.filter(m => m && m.type === 'cms:section-command').map(m => m.command)");
        commands.Should().Contain("add");
    }

    [Fact]
    public async Task SelectingAWidget_ShouldClearTheSectionOutline()
    {
        await using var page = await OpenCanvasAsync(fixture.Browser);
        await page.ClickAsync($"[data-gws-section-handle='{SectionId}']");
        (await page.QuerySelectorAsync(".gws-section-selected")).Should().NotBeNull();

        await page.ClickAsync($"[data-gws-widget-id='{WidgetId}']");

        (await page.QuerySelectorAsync(".gws-section-selected")).Should()
            .BeNull("selecting a widget moves the outline to the widget");
        (await page.QuerySelectorAsync(".gws-editor-selected")).Should().NotBeNull();
    }
}
