using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Drag-to-connect on the workflow canvas. The ports were rendered but inert before this - nothing
// in the codebase read them - so these drive the real automation-editor.js module against markup
// shaped like the editor's and assert on what it posts back to Blazor.
[Collection("Playwright")]
public sealed class AutomationCanvasWiringBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string NodeA = "11111111-1111-1111-1111-111111111111";
    private const string NodeB = "22222222-2222-2222-2222-222222222222";
    private const string NodeC = "33333333-3333-3333-3333-333333333333";
    private const string ConnectionId = "44444444-4444-4444-4444-444444444444";

    private static string ScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/automation-editor.js"));

    private static string Canvas() => $$"""
        <!doctype html><html><head><style>
          .automation-canvas { position:relative; width:900px; height:400px; }
          .automation-connections { position:absolute; inset:0; }
          .automation-node { position:absolute; width:196px; height:110px; background:#222; }
          .node-input,.node-output { position:absolute; width:12px; height:12px; border-radius:50%; background:#789; }
          .node-input { left:-7px; top:49px; } .node-output-main { right:-7px; top:49px; }
          /* An empty span has no size, so without this the handles are ungrabbable in the stub
             even though the real stylesheet gives them 14px. */
          .connection-endpoint { width:14px; height:14px; border-radius:50%; background:#818cf8; }
        </style></head><body>
        <div class="automation-canvas" id="canvas">
          <svg class="automation-connections" width="900" height="400"></svg>
          <article class="automation-node" data-automation-node="{{NodeA}}" style="left:60px; top:60px">
            <div class="node-output node-output-main" data-port="output" data-port-node="{{NodeA}}" data-port-name="main"></div>
            <div class="node-input" data-port="input" data-port-node="{{NodeA}}" data-port-name="main"></div>
          </article>
          <article class="automation-node" data-automation-node="{{NodeB}}" style="left:500px; top:60px">
            <div class="node-output node-output-main" data-port="output" data-port-node="{{NodeB}}" data-port-name="main"></div>
            <div class="node-input" data-port="input" data-port-node="{{NodeB}}" data-port-name="main"></div>
          </article>
          <!-- Endpoint handles for an existing A -> B connection. Each advertises the identity
               of the end that is NOT moving, which is what anchors the drag. -->
          <span class="connection-endpoint" style="position:absolute; left:271px; top:110px"
                data-rewire="source" data-rewire-connection="{{ConnectionId}}"
                data-port="input" data-port-node="{{NodeB}}" data-port-name="main"></span>
          <span class="connection-endpoint" style="position:absolute; left:471px; top:110px"
                data-rewire="target" data-rewire-connection="{{ConnectionId}}"
                data-port="output" data-port-node="{{NodeA}}" data-port-name="main"></span>
          <article class="automation-node" data-automation-node="{{NodeC}}" style="left:500px; top:230px">
            <div class="node-output node-output-main" data-port="output" data-port-node="{{NodeC}}" data-port-name="main"></div>
            <div class="node-input" data-port="input" data-port-node="{{NodeC}}" data-port-name="main"></div>
          </article>
        </div></body></html>
        """;

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", r => r.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = Canvas()
        }));
        await page.GotoAsync("http://localhost/canvas");

        // Stand in for the Blazor circuit and record what the module invokes.
        var module = await File.ReadAllTextAsync(ScriptPath);
        await page.AddScriptTagAsync(new() { Type = "module", Content = module + "\nwindow.gwsCanvas = { initialize, dispose };" });
        await page.WaitForFunctionAsync("() => Boolean(window.gwsCanvas)");
        await page.EvaluateAsync("""
            () => {
              window.__calls = [];
              window.gwsCanvas.initialize(document.getElementById('canvas'), {
                invokeMethodAsync: (name, ...args) => { window.__calls.push({ name, args }); return Promise.resolve(); }
              });
            }
            """);
        return page;
    }

    private static async Task DragAsync(IPage page, string from, string to)
    {
        var a = await (await page.QuerySelectorAsync(from))!.BoundingBoxAsync();
        var b = await (await page.QuerySelectorAsync(to))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(a!.X + a.Width / 2, a.Y + a.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(b!.X + b.Width / 2, b.Y + b.Height / 2, new() { Steps = 12 });
        await page.Mouse.UpAsync();
    }

    [Fact]
    public async Task DraggingOutputToInput_ShouldConnectTheNodes()
    {
        await using var page = await OpenAsync();

        await DragAsync(page,
            $"[data-port-node='{NodeA}'][data-port='output']",
            $"[data-port-node='{NodeB}'][data-port='input']");

        var calls = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts').map(c => c.args.join('|'))");
        calls.Should().ContainSingle().Which.Should().Be($"{NodeA}|main|{NodeB}|main");
    }

    [Fact]
    public async Task DraggingInputToOutput_ShouldNormaliseDirection()
    {
        // Dragging backwards is a normal thing to do; the source must still be the output end.
        await using var page = await OpenAsync();

        await DragAsync(page,
            $"[data-port-node='{NodeB}'][data-port='input']",
            $"[data-port-node='{NodeA}'][data-port='output']");

        var calls = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts').map(c => c.args.join('|'))");
        calls.Should().ContainSingle().Which.Should().Be($"{NodeA}|main|{NodeB}|main");
    }

    [Fact]
    public async Task DraggingOutputToOutput_ShouldBeRejected()
    {
        await using var page = await OpenAsync();

        await DragAsync(page,
            $"[data-port-node='{NodeA}'][data-port='output']",
            $"[data-port-node='{NodeB}'][data-port='output']");

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts').length");
        calls.Should().Be(0, "an output cannot feed another output");
    }

    [Fact]
    public async Task DraggingBackToTheSameNode_ShouldBeRejected()
    {
        await using var page = await OpenAsync();

        await DragAsync(page,
            $"[data-port-node='{NodeA}'][data-port='output']",
            $"[data-port-node='{NodeA}'][data-port='input']");

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts').length");
        calls.Should().Be(0, "a node connected to itself would loop");
    }

    [Fact]
    public async Task DraggingFromAPort_ShouldNotMoveTheNode()
    {
        // The port sits inside the node, so without an explicit port check first the wire drag
        // would be swallowed by the node-move drag that has always been there.
        await using var page = await OpenAsync();
        var before = await page.EvaluateAsync<string>(
            $"() => document.querySelector(\"[data-automation-node='{NodeA}']\").style.left");

        await DragAsync(page,
            $"[data-port-node='{NodeA}'][data-port='output']",
            $"[data-port-node='{NodeB}'][data-port='input']");

        var after = await page.EvaluateAsync<string>(
            $"() => document.querySelector(\"[data-automation-node='{NodeA}']\").style.left");
        after.Should().Be(before);
        var moves = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'UpdateNodePosition').length");
        moves.Should().Be(0);
    }

    [Fact]
    public async Task DraggingANodeBody_ShouldStillMoveIt()
    {
        // Guard against the port handling breaking the drag that already worked.
        await using var page = await OpenAsync();

        var box = await (await page.QuerySelectorAsync($"[data-automation-node='{NodeA}']"))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(box!.X + box.Width / 2, box.Y + box.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width / 2 + 90, box.Y + box.Height / 2 + 40, new() { Steps = 10 });
        await page.Mouse.UpAsync();

        var moves = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'UpdateNodePosition').length");
        moves.Should().Be(1);
    }

    [Fact]
    public async Task DraggingAnEndpointToAnotherPort_ShouldRewireRatherThanConnect()
    {
        await using var page = await OpenAsync();

        // Grab the target end of A -> B and drop it on C's input.
        await DragAsync(page,
            "[data-rewire='target']",
            $"[data-port-node='{NodeC}'][data-port='input']");

        var rewires = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'RewireConnection').map(c => c.args.join('|'))");
        rewires.Should().ContainSingle().Which.Should().Be($"{ConnectionId}|target|{NodeC}|main");

        var connects = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts').length");
        connects.Should().Be(0, "moving an existing wire is a rewire, not a new connection");
    }

    [Fact]
    public async Task DraggingTheSourceEndpoint_ShouldRewireTheSourceEnd()
    {
        await using var page = await OpenAsync();

        await DragAsync(page,
            "[data-rewire='source']",
            $"[data-port-node='{NodeC}'][data-port='output']");

        var rewires = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'RewireConnection').map(c => c.args.join('|'))");
        rewires.Should().ContainSingle().Which.Should().Be($"{ConnectionId}|source|{NodeC}|main");
    }

    [Fact]
    public async Task DraggingAnEndpointOntoAnIncompatiblePort_ShouldBeRejected()
    {
        // The target end is anchored at A's output, so dropping it on another output would be
        // an output-to-output connection.
        await using var page = await OpenAsync();

        await DragAsync(page,
            "[data-rewire='target']",
            $"[data-port-node='{NodeC}'][data-port='output']");

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'RewireConnection').length");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task DraggingAnEndpointBackOntoItsOwnAnchor_ShouldBeRejected()
    {
        // Dropping the target end onto A's own output would connect A to itself.
        await using var page = await OpenAsync();

        await DragAsync(page,
            "[data-rewire='target']",
            $"[data-port-node='{NodeA}'][data-port='output']");

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'RewireConnection').length");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task AnEndpointHandle_ShouldNotBeADropTargetForAnotherWire()
    {
        // Handles carry data-port so one check covers them on pointer-down; they must still be
        // excluded as landing spots or a wire could be dropped onto a handle instead of a port.
        await using var page = await OpenAsync();

        await DragAsync(page,
            $"[data-port-node='{NodeA}'][data-port='output']",
            "[data-rewire='source']");

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts' || c.name === 'RewireConnection').length");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task AWireInFlight_ShouldRenderAPendingPath_AndCleanUpAfterwards()
    {
        await using var page = await OpenAsync();
        var a = await (await page.QuerySelectorAsync($"[data-port-node='{NodeA}'][data-port='output']"))!.BoundingBoxAsync();

        await page.Mouse.MoveAsync(a!.X + a.Width / 2, a.Y + a.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(a.X + 200, a.Y + 60, new() { Steps = 6 });
        (await page.QuerySelectorAsync(".automation-connection.is-pending"))
            .Should().NotBeNull("the wire has to be visible while it is being dragged");

        await page.Mouse.UpAsync();
        (await page.QuerySelectorAsync(".automation-connection.is-pending"))
            .Should().BeNull("a released wire must not leave a stray path behind");
    }
}
