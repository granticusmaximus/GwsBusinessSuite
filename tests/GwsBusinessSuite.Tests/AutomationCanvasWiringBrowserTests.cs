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
          .automation-canvas-viewport { position:relative; width:900px; height:400px; overflow:hidden; }
          .automation-canvas { position:relative; width:900px; height:400px; transform-origin:0 0; }
          .automation-palette { position:absolute; right:0; top:0; width:160px; }
          .palette-node { display:block; width:150px; height:40px; }
          .automation-connections { position:absolute; inset:0; }
          .automation-node { position:absolute; width:196px; height:110px; background:#222; }
          .node-input,.node-output { position:absolute; width:12px; height:12px; border-radius:50%; background:#789; }
          .node-input { left:-7px; top:49px; } .node-output-main { right:-7px; top:49px; }
          /* An empty span has no size, so without this the handles are ungrabbable in the stub
             even though the real stylesheet gives them 14px. */
          .connection-endpoint { width:14px; height:14px; border-radius:50%; background:#818cf8; }
        </style></head><body>
        <input id="inspector" type="text" />
        <aside class="automation-palette">
          <button type="button" class="palette-node"
                  data-palette-node="core.httpRequest" data-palette-version="1" data-palette-label="HTTP Request">HTTP Request</button>
        </aside>
        <div class="automation-canvas-viewport" id="viewport">
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
        </div></div></body></html>
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
        await page.AddScriptTagAsync(new() { Type = "module", Content = module + "\nwindow.gwsCanvas = { initialize, dispose, zoomIn, zoomOut, resetView, fitToContent };" });
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
    public async Task DraggingAPaletteEntryOntoTheCanvas_ShouldAddANodeThere()
    {
        await using var page = await OpenAsync();

        var entry = await (await page.QuerySelectorAsync(".palette-node"))!.BoundingBoxAsync();
        var canvas = await (await page.QuerySelectorAsync("#canvas"))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(entry!.X + entry.Width / 2, entry.Y + entry.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(canvas!.X + 300, canvas.Y + 250, new() { Steps = 12 });
        await page.Mouse.UpAsync();

        var calls = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'AddNodeAt').map(c => c.args.join('|'))");
        calls.Should().ContainSingle();
        // Dropped at canvas (300,250), offset by half a node so it lands centred on the pointer.
        calls[0].Should().Be("core.httpRequest|1|202|195");
    }

    [Fact]
    public async Task ClickingAPaletteEntry_ShouldNotBeTreatedAsADrag()
    {
        // A click with a pixel of pointer wobble must still be a click - the Blazor @onclick
        // handler adds the node, and a drag firing too would add a second one.
        await using var page = await OpenAsync();

        var entry = await (await page.QuerySelectorAsync(".palette-node"))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(entry!.X + entry.Width / 2, entry.Y + entry.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(entry.X + entry.Width / 2 + 2, entry.Y + entry.Height / 2 + 1);
        await page.Mouse.UpAsync();

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'AddNodeAt').length");
        calls.Should().Be(0, "movement under the threshold is a click, not a drag");
    }

    [Fact]
    public async Task DraggingAPaletteEntryOutsideTheCanvas_ShouldAddNothing()
    {
        await using var page = await OpenAsync();

        var entry = await (await page.QuerySelectorAsync(".palette-node"))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(entry!.X + entry.Width / 2, entry.Y + entry.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(entry.X + entry.Width / 2, entry.Y + 320, new() { Steps = 10 });
        await page.Mouse.UpAsync();

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'AddNodeAt').length");
        calls.Should().Be(0, "dropping outside the canvas has no position to create at");
    }

    [Fact]
    public async Task ReleasingAWireOverEmptyCanvas_ShouldOfferAConnectedInsert()
    {
        await using var page = await OpenAsync();

        var port = await (await page.QuerySelectorAsync($"[data-port-node='{NodeA}'][data-port='output']"))!.BoundingBoxAsync();
        var canvas = await (await page.QuerySelectorAsync("#canvas"))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(port!.X + port.Width / 2, port.Y + port.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(canvas!.X + 320, canvas.Y + 320, new() { Steps = 12 });
        await page.Mouse.UpAsync();

        var calls = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'BeginConnectedInsert').map(c => c.args.slice(0,2).join('|'))");
        calls.Should().ContainSingle().Which.Should().Be($"{NodeA}|main");
    }

    [Fact]
    public async Task ReleasingARewireOverEmptyCanvas_ShouldNotCreateANode()
    {
        // Moving an existing endpoint into space means "I changed my mind", not "make me a node".
        await using var page = await OpenAsync();

        var handle = await (await page.QuerySelectorAsync("[data-rewire='target']"))!.BoundingBoxAsync();
        var canvas = await (await page.QuerySelectorAsync("#canvas"))!.BoundingBoxAsync();
        await page.Mouse.MoveAsync(handle!.X + handle.Width / 2, handle.Y + handle.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(canvas!.X + 320, canvas.Y + 330, new() { Steps = 10 });
        await page.Mouse.UpAsync();

        var calls = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'BeginConnectedInsert').length");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task DraggingOnEmptyCanvas_ShouldRubberBandSelectTheNodesItCovers()
    {
        await using var page = await OpenAsync();
        var v = await (await page.QuerySelectorAsync("#viewport"))!.BoundingBoxAsync();

        // A band across both nodes' rows but starting in empty space below them.
        await page.Mouse.MoveAsync(v!.X + 20, v.Y + 40);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(v.X + 800, v.Y + 200, new() { Steps = 10 });
        await page.Mouse.UpAsync();

        var calls = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'SelectNodesInRegion').map(c => c.args[0].slice().sort().join(','))");
        calls.Should().ContainSingle();
        calls[0].Should().Contain(NodeA).And.Contain(NodeB);
    }

    [Fact]
    public async Task AMarqueeThatSelectsNothing_ShouldStillNotThrow()
    {
        await using var page = await OpenAsync();
        var v = await (await page.QuerySelectorAsync("#viewport"))!.BoundingBoxAsync();

        // Well below both nodes.
        await page.Mouse.MoveAsync(v!.X + 40, v.Y + 330);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(v.X + 300, v.Y + 380, new() { Steps = 6 });
        await page.Mouse.UpAsync();

        var ids = await page.EvaluateAsync<int>(
            "() => { const c = window.__calls.filter(x => x.name === 'SelectNodesInRegion'); return c.length ? c[0].args[0].length : -1; }");
        ids.Should().Be(0, "an empty band clears the selection rather than doing nothing");
    }

    [Fact]
    public async Task ScrollingTheCanvas_ShouldZoomIt()
    {
        await using var page = await OpenAsync();
        var v = await (await page.QuerySelectorAsync("#viewport"))!.BoundingBoxAsync();

        await page.Mouse.MoveAsync(v!.X + 400, v.Y + 200);
        await page.Mouse.WheelAsync(0, -240);

        var zoom = await page.EvaluateAsync<double>(
            "() => parseFloat(document.getElementById('canvas').dataset.zoom)");
        zoom.Should().BeGreaterThan(1.0, "scrolling up zooms in");
    }

    [Fact]
    public async Task ConnectingWhileZoomed_ShouldStillHitTheRightPorts()
    {
        // The whole reason coordinates go through one conversion: at any zoom other than 1, an
        // offset-based calculation lands the wire somewhere else entirely.
        await using var page = await OpenAsync();
        await page.EvaluateAsync("() => window.gwsCanvas.zoomOut(document.getElementById('canvas'))");
        await page.EvaluateAsync("() => window.gwsCanvas.zoomOut(document.getElementById('canvas'))");

        await DragAsync(page,
            $"[data-port-node='{NodeA}'][data-port='output']",
            $"[data-port-node='{NodeB}'][data-port='input']");

        var calls = await page.EvaluateAsync<string[]>(
            "() => window.__calls.filter(c => c.name === 'ConnectPorts').map(c => c.args.join('|'))");
        calls.Should().ContainSingle().Which.Should().Be($"{NodeA}|main|{NodeB}|main");
    }

    [Fact]
    public async Task DraggingANodeWhileZoomed_ShouldTrackThePointer()
    {
        // A node dragged at 0.7 zoom must follow the cursor, not lag or overshoot by the scale
        // factor - the classic symptom of mixing screen and canvas space.
        await using var page = await OpenAsync();
        await page.EvaluateAsync("() => window.gwsCanvas.zoomOut(document.getElementById('canvas'))");

        var box = await (await page.QuerySelectorAsync($"[data-automation-node='{NodeA}']"))!.BoundingBoxAsync();
        var startLeft = await page.EvaluateAsync<double>(
            $"() => parseFloat(document.querySelector(\"[data-automation-node='{NodeA}']\").style.left)");

        await page.Mouse.MoveAsync(box!.X + box.Width / 2, box.Y + box.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width / 2 + 120, box.Y + box.Height / 2, new() { Steps = 10 });
        await page.Mouse.UpAsync();

        var zoom = await page.EvaluateAsync<double>("() => parseFloat(document.getElementById('canvas').dataset.zoom)");
        var endLeft = await page.EvaluateAsync<double>(
            $"() => parseFloat(document.querySelector(\"[data-automation-node='{NodeA}']\").style.left)");

        // 120 screen px at this zoom is 120/zoom canvas px.
        (endLeft - startLeft).Should().BeApproximately(120 / zoom, 4);
    }

    [Fact]
    public async Task FitToContent_ShouldBringEveryNodeIntoView()
    {
        await using var page = await OpenAsync();
        await page.EvaluateAsync("() => window.gwsCanvas.fitToContent(document.getElementById('canvas'))");

        var visible = await page.EvaluateAsync<bool>("""
            () => {
              const v = document.getElementById('viewport').getBoundingClientRect();
              return [...document.querySelectorAll('[data-automation-node]')].every(el => {
                const r = el.getBoundingClientRect();
                return r.left >= v.left - 1 && r.right <= v.right + 1 && r.top >= v.top - 1 && r.bottom <= v.bottom + 1;
              });
            }
            """);
        visible.Should().BeTrue("fit has to actually fit every node inside the viewport");
    }

    [Fact]
    public async Task AMarqueeDrag_ShouldSwallowTheClickThatFollowsIt()
    {
        // The canvas clears selection on click, and a drag still ends in one - so without
        // swallowing it the rubber-band selection is wiped the moment it is made.
        await using var page = await OpenAsync();
        await page.EvaluateAsync(
            "() => { window.__clicks = 0; document.getElementById('canvas').addEventListener('click', () => window.__clicks++); }");
        var v = await (await page.QuerySelectorAsync("#viewport"))!.BoundingBoxAsync();

        await page.Mouse.MoveAsync(v!.X + 20, v.Y + 40);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(v.X + 800, v.Y + 200, new() { Steps = 10 });
        await page.Mouse.UpAsync();

        var selections = await page.EvaluateAsync<int>(
            "() => window.__calls.filter(c => c.name === 'SelectNodesInRegion').length");
        selections.Should().Be(1, "the marquee still selects");

        var clicks = await page.EvaluateAsync<int>("() => window.__clicks");
        clicks.Should().Be(0, "the click that would clear that selection must not reach the canvas");
    }

    [Fact]
    public async Task AnOrdinaryCanvasClick_ShouldStillReachTheCanvas()
    {
        // The guard must swallow exactly one click after a marquee, not suppress clearing
        // selection by clicking empty space.
        await using var page = await OpenAsync();
        await page.EvaluateAsync(
            "() => { window.__clicks = 0; document.getElementById('canvas').addEventListener('click', () => window.__clicks++); }");
        var v = await (await page.QuerySelectorAsync("#viewport"))!.BoundingBoxAsync();

        await page.Mouse.ClickAsync(v!.X + 40, v.Y + 350);

        (await page.EvaluateAsync<int>("() => window.__clicks")).Should().Be(1);
    }

    [Fact]
    public async Task SpaceInATextField_ShouldNotArmPanning()
    {
        // Space is a character when a field has focus. Arming pan from it makes the next canvas
        // drag pan instead of selecting or moving a node.
        await using var page = await OpenAsync();
        await page.FocusAsync("#inspector");
        await page.Keyboard.PressAsync("Space");

        var pannable = await page.EvaluateAsync<bool>(
            "() => document.getElementById('viewport').classList.contains('is-pannable')");
        pannable.Should().BeFalse();
    }

    [Fact]
    public async Task SpaceOnTheCanvas_ShouldArmPanning()
    {
        await using var page = await OpenAsync();
        await page.EvaluateAsync("() => document.getElementById('canvas').focus()");
        await page.Keyboard.DownAsync("Space");

        var pannable = await page.EvaluateAsync<bool>(
            "() => document.getElementById('viewport').classList.contains('is-pannable')");
        pannable.Should().BeTrue();

        await page.Keyboard.UpAsync("Space");
        (await page.EvaluateAsync<bool>("() => document.getElementById('viewport').classList.contains('is-pannable')"))
            .Should().BeFalse("releasing space disarms it again");
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
