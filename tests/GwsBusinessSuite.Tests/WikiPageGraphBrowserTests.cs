using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Loads wiki-page-graph.js directly into a bare page (bypassing Blazor entirely), matching
// WikiBlockEditorBrowserTests's own pattern - the module's canvas rendering and hit-testing are
// real DOM/Canvas behaviour that a C# unit test cannot exercise at all, but a full Blazor circuit
// is unnecessary overhead just to prove the module draws and dispatches clicks correctly.
[Collection("Playwright")]
public sealed class WikiPageGraphBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static async Task<IPage> LoadModuleAsync(PlaywrightBrowserFixture fixture)
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = """<main><canvas id="graph" style="width:640px;height:420px"></canvas></main>"""
        }));
        await page.GotoAsync("http://localhost/graph");

        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/wiki-page-graph.js"));
        var moduleSource = await File.ReadAllTextAsync(scriptPath);
        moduleSource = moduleSource.Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelPageGraph = { render, initialize, dispose };";
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Content = moduleSource });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelPageGraph)");
        return page;
    }

    private static object BuildGraph(Guid centerId, Guid linkedId) => new
    {
        nodes = new object[]
        {
            new { pageId = centerId, title = "Runbook", icon = "📄", isCenter = true },
            new { pageId = linkedId, title = "Escalation Policy", icon = "📄", isCenter = false }
        },
        edges = new object[] { new { sourcePageId = centerId, targetPageId = linkedId } }
    };

    [Fact]
    public async Task Render_ShouldDrawWithoutThrowing_AndSizeTheCanvasToItsContainer()
    {
        await using var page = await LoadModuleAsync(fixture);
        var centerId = Guid.NewGuid();
        var linkedId = Guid.NewGuid();

        var canvasSize = await page.EvaluateAsync<int[]>(
            """
            (graph) => {
                const canvas = document.getElementById('graph');
                window.sentinelPageGraph.render(canvas, graph);
                return [canvas.width, canvas.height];
            }
            """,
            BuildGraph(centerId, linkedId));

        canvasSize[0].Should().BeGreaterThan(0);
        canvasSize[1].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Render_ShouldDoNothingHarmful_WhenGivenAnEmptyGraph()
    {
        await using var page = await LoadModuleAsync(fixture);

        var threw = await page.EvaluateAsync<bool>(
            """
            () => {
                try {
                    const canvas = document.getElementById('graph');
                    window.sentinelPageGraph.render(canvas, { nodes: [], edges: [] });
                    return false;
                } catch {
                    return true;
                }
            }
            """);

        threw.Should().BeFalse("a page with no links yet must render an empty canvas, not throw");
    }

    [Fact]
    public async Task Initialize_ShouldNavigateToTheClickedNode_ButNotToTheCenterNode()
    {
        await using var page = await LoadModuleAsync(fixture);
        var centerId = Guid.NewGuid();
        var linkedId = Guid.NewGuid();

        // A fake DotNetObjectReference recording every invokeMethodAsync call, since this is a
        // bare-page test with no real Blazor circuit behind it.
        var invokedIds = await page.EvaluateAsync<string[]>(
            """
            async (graph) => {
                const canvas = document.getElementById('graph');
                const calls = [];
                const fakeDotNetRef = { invokeMethodAsync: (method, id) => { calls.push(id); return Promise.resolve(); } };
                window.sentinelPageGraph.initialize(canvas, fakeDotNetRef, graph);

                const rect = canvas.getBoundingClientRect();
                const centerNode = graph.nodes.find(n => n.isCenter);
                const otherNode = graph.nodes.find(n => !n.isCenter);

                function clickAt(x, y) {
                    canvas.dispatchEvent(new MouseEvent('click', { clientX: rect.left + x, clientY: rect.top + y }));
                }
                // Positions are recomputed by the force simulation, so read them back off the
                // module's own layout by re-rendering and inspecting - simplest reliable way to
                // click exactly on a node without duplicating the physics in this test.
                const layout = window.sentinelPageGraph.render(canvas, graph);
                for (const node of layout.nodes) {
                    clickAt(node.x, node.y);
                }
                await new Promise(resolve => setTimeout(resolve, 0));
                return calls;
            }
            """,
            BuildGraph(centerId, linkedId));

        invokedIds.Should().ContainSingle().Which.Should().Be(linkedId.ToString(),
            "clicking the center node must not navigate anywhere - it is already the open page");
    }

    [Fact]
    public async Task Dispose_ShouldRemoveTheClickListener_SoALaterClickDoesNothing()
    {
        await using var page = await LoadModuleAsync(fixture);
        var centerId = Guid.NewGuid();
        var linkedId = Guid.NewGuid();

        var invokedAfterDispose = await page.EvaluateAsync<int>(
            """
            async (graph) => {
                const canvas = document.getElementById('graph');
                let calls = 0;
                const fakeDotNetRef = { invokeMethodAsync: () => { calls++; return Promise.resolve(); } };
                window.sentinelPageGraph.initialize(canvas, fakeDotNetRef, graph);
                window.sentinelPageGraph.dispose(canvas);

                const rect = canvas.getBoundingClientRect();
                const otherNode = graph.nodes.find(n => !n.isCenter);
                canvas.dispatchEvent(new MouseEvent('click', { clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2 }));
                await new Promise(resolve => setTimeout(resolve, 0));
                return calls;
            }
            """,
            BuildGraph(centerId, linkedId));

        invokedAfterDispose.Should().Be(0);
    }
}
