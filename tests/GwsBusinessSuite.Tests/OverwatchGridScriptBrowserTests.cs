using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Regression guard for three real incidents, all only reproducible against the real shipped
// script/CSS - there is no server-side signal for any of them:
//
// 1. OpenStreetMapImageryProvider.fromUrl doesn't exist on the pinned Cesium version (it's
//    still a plain synchronous constructor, unlike several other Cesium imagery providers), so
//    init() threw synchronously the moment anyone opened Overwatch. Left uncaught, that
//    JSException-from-interop crashed the whole Blazor Server circuit - reported as "nothing
//    opens" for the *entire app*, not just this one page.
// 2. CesiumJS calls eval()/new Function() internally and separately compiles a WebAssembly
//    module at startup - both flatly blocked by this app's default CSP (no 'unsafe-eval', no
//    'wasm-unsafe-eval'). The FIRST fix above shipped without this CSP header applied at all in
//    its own test, so it still silently passed while the real page kept failing in production -
//    the exact gap this harness now closes by sending the real header, not a CSP-free stub.
// 3. Blazor's CSS isolation compiler does not correctly strip the scope attribute from
//    :global(...) selectors in this project's toolchain - every :global() rule that used to
//    live in OverwatchGrid.razor.css (targeting the JS-injected modal/panels/tooltips) compiled
//    to `.some-class[b-xxxxx]`, which never matches an element created via plain
//    document.createElement. This harness's own hand-duplicated CSS below (see its next
//    comment) was written independently of that scoped file and never went through Blazor's
//    isolation pipeline at all, so it accidentally sidestepped the bug entirely - 24+ passing
//    tests here never caught a production bug that made every stream/incident/watch-wall panel
//    render unstyled and mispositioned. The real fix moved that styling to a genuinely plain,
//    non-isolated stylesheet (wwwroot/tactical-globe.css, loaded via a normal <link> tag) - this
//    harness's duplication is now less likely to silently diverge from a broken source of truth,
//    but is still worth double-checking against the real file if a future change here passes
//    while the real page doesn't.
//
// The harness loads tactical-globe.js as an external <script src>, exactly like the real page
// (OverwatchGrid.razor), and drives init()/etc. via page.EvaluateAsync from outside the page's
// own HTML rather than an inline <script> tag in the document body - an inline tag would trip
// this same CSP's lack of 'unsafe-inline' in script-src as a harness artifact having nothing to
// do with whether the real page (which never inlines a script either - Blazor's JS interop
// calls in from outside the parsed document, not via a script element) actually works.
//
// Mirrors CmsCanvasSectionSelectionBrowserTests.cs's own "drive the real script in a browser"
// approach. See Program.cs for the actual route-scoped CSP this duplicates.
[Collection("Playwright")]
public sealed class OverwatchGridScriptBrowserTests(PlaywrightBrowserFixture fixture)
{
    // Kept in sync with Program.cs's CSP for the /admin/osint route by hand - there's no shared
    // constant to import across the Web/Test project boundary. If this test ever passes while
    // the real page fails, check this string against Program.cs's script-src first.
    private const string OverwatchGridCsp =
        "default-src 'self'; " +
        "script-src 'self' https://cdn.jsdelivr.net 'unsafe-eval' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "img-src 'self' data: blob: https:; " +
        "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; " +
        "connect-src 'self' wss: ws: https://nominatim.openstreetmap.org https://*.azurewebsites.net https://cdn.jsdelivr.net https://server.arcgisonline.com https://tile.openstreetmap.org https://*.tile.openstreetmap.org https://mesonet.agron.iastate.edu; " +
        "media-src 'self' blob: https:; " +
        "worker-src 'self' blob: https://cdn.jsdelivr.net; " +
        "frame-src 'self'; " +
        "frame-ancestors 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';";

    private static string ScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

    // Real production styling for the drag/resize/modal-relevant classes lives in
    // wwwroot/tactical-globe.css (a genuinely plain, non-Blazor-scoped stylesheet - see incident
    // 3 above for why it's not in OverwatchGrid.razor.css), which this bare-JS harness never
    // loads - without it, .tg-modal-backdrop/.tg-stream-panel/.tg-watch-wall-panel etc. have no
    // `position: absolute` at all, so style.left/top set by makeDraggableAndResizable (all three:
    // the watch wall and, since drag/resize was added back to the single-camera/incident modal,
    // openAsModal's own initial-centering math too) would be entirely inert (a real gap this
    // duplicates just enough of to make both testable, confirmed empirically: the drag/resize
    // tests read a stale, unpositioned 0-diff before this was added).
    private const string HarnessHtml = """
        <!doctype html>
        <html>
        <head>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Widgets/widgets.css" />
          <script src="https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Cesium.js"></script>
          <script src="/js/tactical-globe.js"></script>
          <style>
            /* A real, found-the-hard-way gap: without this, the browser's default 8px <body>
               margin shifts .tg-shell (and therefore the canvas) 8px away from the (0,0) every
               hardcoded click coordinate in this file assumes - close enough that a 14px `point`
               entity's pick tolerance happened to absorb it, but a same-sized `billboard` entity's
               tighter pick precision (see tactical-globe.js's own comment on pinBillboardImage)
               does not, surfacing as click/pick timeouts across most of this file's tests. */
            body { margin: 0; }
            .tg-modal-backdrop { position: absolute; inset: 0; }
            .tg-stream-panel { position: absolute; width: 320px; min-width: 240px; min-height: 160px; }
            .tg-weather-panel { position: absolute; bottom: 2.6rem; left: 1.1rem; display: none; }
            .tg-panel-resize-handle { position: absolute; right: 2px; bottom: 2px; width: 14px; height: 14px; }
            .tg-watch-wall-panel { position: absolute; top: 3.6rem; right: 1.1rem; width: 560px; height: 420px; min-width: 320px; min-height: 240px; }
            .tg-watch-wall-grid { display: grid; }
            .tg-selection-indicator { position: absolute; bottom: 2.6rem; right: 1.1rem; }
            .tg-radar-bar { position: absolute; z-index: 7; left: 1rem; right: 1rem; bottom: 2.6rem; display: none; }
          </style>
        </head>
        <body>
          <div class="tg-shell" style="position:relative;width:800px;height:600px;">
            <div id="tg-viewport" style="position:absolute;inset:0;"></div>
          </div>
          <button data-tg-toggle="radar">radar</button>
          <button data-tg-toggle="alerts">alerts</button>
          <button data-tg-toggle="incidents">incidents</button>
          <button data-tg-toggle="coverage">coverage</button>
          <button data-tg-toggle="select">select</button>
          <input type="text" id="tg-search-input" />
          <button type="button" id="tg-search-button">GO</button>
          <button type="button" id="tg-share-button">SHARE VIEW</button>
          <button type="button" data-tg-streetview draggable="true" style="width:40px;height:40px">person</button>
          <span id="tg-search-status"></span>
        </body>
        </html>
        """;

    // Every caller must dispose the returned page (`await using var pageScope = page;` right
    // after the call) - a real bug found in this file: with 21 tests sharing one IBrowser
    // (PlaywrightBrowserFixture) and none of them ever closing their page, every prior test's
    // Cesium globe (its own WebGL context, requestAnimationFrame loop, and setInterval-based
    // snapshot refresh) stayed alive for the rest of the process, so by the last few tests in a
    // full run there could be a dozen-plus live globes competing for the same renderer process -
    // this reproduced as real, non-deterministic click/waitFor timeouts specifically on later
    // tests, while every test still passed reliably in isolation. Every other Playwright test
    // file in this repo already uses `await using var page = await fixture.Browser.NewPageAsync();`
    // directly; this file can't do that inline since page creation lives in this shared helper,
    // hence the two-line pattern at each call site instead.
    private static async Task<(IPage Page, List<string> ConsoleErrors)> OpenHarnessAsync(IBrowser browser)
    {
        var script = await File.ReadAllTextAsync(ScriptPath);

        var page = await browser.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, msg) => { if (msg.Type == "error") consoleErrors.Add(msg.Text); };

        await page.RouteAsync("http://localhost/overwatch-grid-harness", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = OverwatchGridCsp },
            Body = HarnessHtml
        }));
        await page.RouteAsync("http://localhost/js/tactical-globe.js", route => route.FulfillAsync(new()
        {
            Status = 200,
            // The script's own text has "·" separators; without a charset they decode as "Â·".
            ContentType = "application/javascript; charset=utf-8",
            Body = script
        }));
        // Close zooms ask Esri for imagery and road-label tiles, and Esri sometimes answers one
        // without its CORS header, which Chrome logs as a console error (a pre-push run, and CI on
        // 29-10-26, failed on exactly that). Every test gets those tiles as a blank image, so the
        // tests check the script, not Esri's servers.
        await page.RouteAsync("https://server.arcgisonline.com/**/tile/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "image/png",
            Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
            BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==")
        }));

        await page.GotoAsync("http://localhost/overwatch-grid-harness");
        await page.WaitForFunctionAsync("!!window.tacticalGlobe", new PageWaitForFunctionOptions { Timeout = 10000 });
        return (page, consoleErrors);
    }

    // init() is async (it awaits Cesium.ArcGisMapServerImageryProvider.fromUrl for the Esri
    // World_Imagery aerial basemap - see tactical-globe.js), matching how Blazor's own
    // JS.InvokeVoidAsync awaits it in production. Awaiting it here too is what actually lets
    // this try/catch observe a rejection - a fire-and-forget call would only catch a
    // synchronous throw before init's first await, silently missing anything after it.
    // The stubbed dotNetRef's invokeMethodAsync answers 'GeocodeAsync'/'ReverseGeocodeAsync' from
    // window.__mockGeocodeResult/__mockPlaceInfo (set per-test) rather than a mocked HTTP route -
    // both now happen via [JSInvokable] calls into the real Blazor component (see GeocodeAsync/
    // ReverseGeocodeAsync on OverwatchGrid.razor), not a client-side fetch(), so there is no HTTP
    // request for this harness to intercept. Every call is recorded in window.__invokedMethods so
    // tests can assert whether (and with what arguments) a given method was actually called.
    private static async Task<string?> InitAsync(IPage page) => await page.EvaluateAsync<string?>("""
        async () => {
          window.__invokedMethods = [];
          try {
            await window.tacticalGlobe.init('tg-viewport', {
              invokeMethodAsync: function (methodName, ...args) {
                window.__invokedMethods.push({ methodName: methodName, args: args });
                if (methodName === 'GeocodeAsync') {
                  return Promise.resolve(window.__mockGeocodeResult || null);
                }
                if (methodName === 'ReverseGeocodeAsync') {
                  return Promise.resolve(window.__mockPlaceInfo || null);
                }
                if (methodName === 'ToggleCameraFavoriteAsync') {
                  return Promise.resolve(window.__mockToggleFavoriteResult === undefined ? true : window.__mockToggleFavoriteResult);
                }
                if (methodName === 'GetAircraftDetailAsync') {
                  return Promise.resolve(window.__mockAircraftDetail || null);
                }
                if (methodName === 'BuildShareLinkAsync') {
                  return Promise.resolve(window.__mockShareLink || 'https://example.test/admin/osint?v=stub');
                }
                if (window.__mockInvoke && Object.prototype.hasOwnProperty.call(window.__mockInvoke, methodName)) {
                  return Promise.resolve(window.__mockInvoke[methodName]);
                }
                if (methodName === 'AnalyzeCameraAsync') {
                  var analyzeResult = window.__mockAnalyzeResult === undefined ? 'light traffic, clear skies' : window.__mockAnalyzeResult;
                  var delayMs = window.__mockAnalyzeDelayMs || 0;
                  return new Promise(function (resolve) { setTimeout(function () { resolve(analyzeResult); }, delayMs); });
                }
                return Promise.resolve();
              }
            });
            return null;
          } catch (e) {
            return e.message;
          }
        }
        """);

    [Fact]
    public async Task StreetView_DraggingThePersonOntoTheGlobe_ShouldReportWhereItLanded_AndMarkIt()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();
        await page.EvaluateAsync("() => window.tacticalGlobe.enableStreetViewDrop('tg-viewport')");
        await page.WaitForTimeoutAsync(500);

        // The globe opens centred on 39.8, -98.5; dropping on the middle of the canvas lands near there.
        await page.DragAndDropAsync("[data-tg-streetview]", "#tg-viewport", new() { TargetPosition = new() { X = 400, Y = 300 } });
        await page.WaitForFunctionAsync("window.__invokedMethods.some(c => c.methodName === 'OnStreetViewDropped')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
        var args = await page.EvaluateAsync<double[]>("window.__invokedMethods.find(c => c.methodName === 'OnStreetViewDropped').args");
        args[0].Should().BeApproximately(39.8, 3);
        args[1].Should().BeApproximately(-98.5, 3);

        await page.EvaluateAsync("() => { window.tacticalGlobe.setStreetViewMarker('tg-viewport', 39.8, -98.5); window.tacticalGlobe.clearStreetViewMarker('tg-viewport'); }");
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task MapControls_ZoomAndHome_ShouldMoveTheCamera()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();
        await page.WaitForTimeoutAsync(500);

        await page.EvaluateAsync("() => window.tacticalGlobe.zoom('tg-viewport', true)");
        await page.WaitForFunctionAsync("window.__invokedMethods.some(c => c.methodName === 'OnGlobeViewChanged')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
        await page.EvaluateAsync("() => { window.tacticalGlobe.zoom('tg-viewport', false); window.tacticalGlobe.home('tg-viewport'); }");
        consoleErrors.Should().BeEmpty();
    }

    // Performance guard (Overwatch standout item 11): a busy view with every layer on - 1,500
    // camera pins, 400 aircraft, 20 air quality points, 60 outage counties and 50 map notes - must
    // draw quickly against the real Cesium build, and a small pan (most pins unchanged) must be
    // much cheaper than the first draw. Bounds are generous so a loaded CI machine still passes;
    // the timings are printed for comparison.
    // Average time between 10 animation frames, in ms.
    private const string FrameTimeScript = """
        () => new Promise(resolve => {
            const start = performance.now();
            let frames = 0;
            const tick = () => { frames++; if (frames === 10) resolve((performance.now() - start) / 10); else requestAnimationFrame(tick); };
            requestAnimationFrame(tick);
        })
        """;

    [Fact]
    public async Task ManyLayers_ShouldDrawABusyViewQuickly()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();
        await page.WaitForTimeoutAsync(1500);
        var emptyFrame = await page.EvaluateAsync<double>(FrameTimeScript);

        var timingsJson = await page.EvaluateAsync<string>("""
            () => {
                const g = window.tacticalGlobe;
                const pins = [];
                for (let i = 0; i < 1500; i++) {
                    pins.push({ id: 'cam-' + i, name: 'Camera ' + i, lat: 33 + (i % 50) * 0.02, lon: -85 + Math.floor(i / 50) * 0.02,
                        streamUrl: 'https://cams.example/' + i + '.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: '', isFavorite: false, health: 'Unknown' });
                }
                const time = (fn) => { const t = performance.now(); fn(); return performance.now() - t; };
                const result = {};
                result.cameras = time(() => g.setCameraPins(pins));
                const panned = pins.slice(50).concat(pins.slice(0, 50).map(p => Object.assign({}, p, { id: p.id + '-new' })));
                result.camerasPan = time(() => g.setCameraPins(panned));
                const planes = [];
                for (let i = 0; i < 400; i++) planes.push({ id: 'p' + i, callsign: 'DAL' + i, lat: 33 + Math.random(), lon: -85 + Math.random(), altitudeFeet: 30000, speedKnots: 450, heading: i % 360 });
                result.aircraft = time(() => g.setAircraft('tg-viewport', planes));
                result.aircraftRefresh = time(() => g.setAircraft('tg-viewport', planes));
                const aqi = [];
                for (let i = 0; i < 20; i++) aqi.push({ lat: 33 + i * 0.05, lon: -84, aqi: i * 15, category: 'x' });
                result.airQuality = time(() => g.setAirQuality('tg-viewport', aqi));
                const counties = [];
                for (let i = 0; i < 60; i++) {
                    const ring = [];
                    for (let k = 0; k < 400; k++) { const a = k / 400 * Math.PI * 2; ring.push(-84 + (i % 10) * 0.3 + Math.cos(a) * 0.1, 33 + Math.floor(i / 10) * 0.3 + Math.sin(a) * 0.1); }
                    counties.push({ name: 'COUNTY ' + i, customersOut: i * 100, lat: 33, lon: -84, rings: [ring] });
                }
                result.outages = time(() => g.setPowerOutages('tg-viewport', counties));
                const notes = [];
                for (let i = 0; i < 50; i++) notes.push({ id: 'n' + i, kind: i % 2 ? 'pin' : 'area', title: 'Note ' + i, color: '#ffd166',
                    points: i % 2 ? [[33 + i * 0.01, -84]] : [[33, -84 + i * 0.01], [33.05, -84 + i * 0.01], [33.02, -83.97 + i * 0.01]] });
                result.notes = time(() => g.setMapNotes('tg-viewport', notes, null));
                return JSON.stringify(result);
            }
            """);
        var timings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(timingsJson)!;
        await page.WaitForTimeoutAsync(500);
        var frame = await page.EvaluateAsync<double>(FrameTimeScript);

        Console.WriteLine("Overwatch draw timings (ms): " + string.Join(", ", timings.Select(t => $"{t.Key} {t.Value:0}")) + $", frame empty {emptyFrame:0} busy {frame:0}");
        timings["cameras"].Should().BeLessThan(3000);
        timings["camerasPan"].Should().BeLessThan(timings["cameras"], "a pan only touches the pins that changed");
        timings["aircraftRefresh"].Should().BeLessThan(2000);
        timings["outages"].Should().BeLessThan(3000);
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task AirLayers_ShouldDrawAndClearAircraftAndAirQuality()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        var error = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    window.tacticalGlobe.setAircraft('tg-viewport', [
                        { id: 'ad9db8', callsign: 'DAL3173', lat: 33.6, lon: -84.15, altitudeFeet: 14475, speedKnots: 374, heading: 45 },
                        { id: 'a00002', callsign: '', lat: 33.7, lon: -84.3, altitudeFeet: null, speedKnots: null, heading: null }
                    ]);
                    window.tacticalGlobe.setAirQuality('tg-viewport', [
                        { lat: 33.8, lon: -84.4, aqi: 31, category: 'Good' }, { lat: 34.2, lon: -84.2, aqi: 412, category: 'Hazardous' }
                    ]);
                    window.tacticalGlobe.setPowerOutages('tg-viewport', [
                        { name: 'MACON COUNTY', customersOut: 2076, lat: 35.1, lon: -83.4, rings: [[-83.7, 35.0, -83.1, 35.0, -83.1, 35.3], [-83.0, 35.1, -82.9, 35.1, -82.9, 35.2]] },
                        { name: 'BAD', customersOut: 5, lat: 0, lon: 0, rings: [[1, 2]] }
                    ]);
                    window.tacticalGlobe.setAircraft('tg-viewport', []);
                    window.tacticalGlobe.setAirQuality('tg-viewport', []);
                    window.tacticalGlobe.setPowerOutages('tg-viewport', []);
                    return null;
                } catch (e) { return String(e && e.stack || e); }
            }
            """);
        await page.WaitForTimeoutAsync(500);

        error.Should().BeNull();
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ClickingAnAircraft_ShouldShowItsDetails_AndDrawItsPath()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        // The globe opens centred on 39.8, -98.5: a plane there sits in the middle of the canvas.
        await page.EvaluateAsync("""
            () => {
                // Counts the entities the panel draws (everything else on the globe was added before this).
                window.__pathEntities = new Set();
                const add = Cesium.EntityCollection.prototype.add;
                Cesium.EntityCollection.prototype.add = function (e) { const added = add.call(this, e); window.__pathEntities.add(added); return added; };
                const remove = Cesium.EntityCollection.prototype.remove;
                Cesium.EntityCollection.prototype.remove = function (e) { window.__pathEntities.delete(e); return remove.call(this, e); };
                window.__mockAircraftDetail = {
                    icao24: 'ab4b34', callsign: 'DAL1536', use: 'COMMERCIAL - PASSENGER AIRLINE', isCommercial: true,
                    flightNumber: 'DL1536 (DAL1536)', airline: 'Delta Air Lines', registration: 'N827DN',
                    aircraftType: 'Boeing 737NG 932ER/W', typeCode: 'B739', operator: 'Delta Air Lines', typicalSeats: '177-220',
                    route: {
                        origin: { code: 'STL', city: 'St Louis', latitude: 38.75, longitude: -90.37 }, originSource: 'TRACKED TAKEOFF',
                        destination: { code: 'DEN', city: 'Denver', latitude: 39.86, longitude: -104.67 }, destinationSource: 'ON FILE, HEADING THERE',
                        note: 'The route on file for this flight number (JFK \u2192 DEN) starts somewhere else, but the plane is heading for DEN.'
                    },
                    track: [{ latitude: 38.75, longitude: -90.37, altitudeFeet: 0 }, { latitude: 39.5, longitude: -95, altitudeFeet: 35000 }]
                };
                window.tacticalGlobe.setAircraft('tg-viewport', [
                    { id: 'ab4b34', callsign: 'DAL1536', lat: 39.8, lon: -98.5, altitudeFeet: 35000, speedKnots: 450, heading: 270 }
                ]);
            }
            """);
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForSelectorAsync(".tg-aircraft-panel dd:text('N827DN')", new PageWaitForSelectorOptions { Timeout = 5000 });

        var call = await page.EvaluateAsync<string[]>(
            "window.__invokedMethods.find(c => c.methodName === 'GetAircraftDetailAsync').args.slice(0, 2)");
        call.Should().Equal("ab4b34", "DAL1536");
        var panelText = await page.InnerTextAsync(".tg-aircraft-panel");
        panelText.Should().Contain("COMMERCIAL - PASSENGER AIRLINE").And.Contain("DL1536").And.Contain("177-220")
            .And.Contain("NOT PUBLISHED").And.Contain("STL (ST LOUIS) - TRACKED TAKEOFF").And.Contain("DEN (DENVER) - ON FILE, HEADING THERE").And.Contain("starts somewhere else").And.Contain("35,000 FT");
        // Track (solid), remaining leg (dashed), and the two airports.
        (await page.EvaluateAsync<int>("window.__pathEntities.size")).Should().Be(4);
        (await page.IsVisibleAsync(".tg-modal-backdrop")).Should().BeFalse("the path has to stay visible behind the panel");

        // The minute's refresh moves the plane and its readings.
        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setAircraft('tg-viewport', [
                { id: 'ab4b34', callsign: 'DAL1536', lat: 39.8, lon: -99.5, altitudeFeet: 36000, speedKnots: 455, heading: 270 }
            ])
            """);
        (await page.InnerTextAsync(".tg-aircraft-panel [data-live='alt']")).Should().Be("36,000 FT");

        await page.ClickAsync(".tg-aircraft-panel .tg-stream-panel-close");
        (await page.QuerySelectorAsync(".tg-aircraft-panel")).Should().BeNull();
        (await page.EvaluateAsync<int>("window.__pathEntities.size")).Should().Be(0);
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task MapNotes_ShouldDrawPinsAreasAndADraft_AndReportClicksOnThem()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        // The globe opens centred on 39.8, -98.5: a pin there sits in the middle of the canvas.
        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setMapNotes('tg-viewport', [
                { id: 'pin-1', kind: 'pin', title: 'Staging', color: '#ff4d4d', points: [[39.8, -98.5]] },
                { id: 'area-1', kind: 'area', title: 'Blind spot', color: '#7dffb0', points: [[30, -110], [31, -110], [30.5, -109]] }
            ], { points: [[35, -100], [36, -100]], color: '#ffd166' })
            """);
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForFunctionAsync("window.__invokedMethods.some(c => c.methodName === 'OnMapNoteClicked')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
        (await page.EvaluateAsync<string>("window.__invokedMethods.find(c => c.methodName === 'OnMapNoteClicked').args[0]")).Should().Be("pin-1");

        await page.EvaluateAsync("() => window.tacticalGlobe.setMapNotes('tg-viewport', [], null)");
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ThreeD_WithoutAToken_ShouldStayOff_AndTheStreetLevelFlightShouldFinish()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', true)")).Should().Be("no-token");
        var flight = await page.EvaluateAsync<double>("""
            async () => {
                const started = performance.now();
                await window.tacticalGlobe.flyToStreetLevel('tg-viewport', 33.75, -84.39, 90);
                return performance.now() - started;
            }
            """);
        flight.Should().BeLessThan(8000, "street view waits for this flight, so it must always finish");
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ThreeD_WithAnUnusableToken_ShouldFailQuietly_AndKeepTheFlatGlobe()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await page.RouteAsync("https://api.cesium.com/**", route => route.FulfillAsync(new() { Status = 401, ContentType = "application/json", Body = """{"code":"InvalidCredentials"}""" }));
        var error = await page.EvaluateAsync<string?>("""
            async () => {
                try {
                    await window.tacticalGlobe.init('tg-viewport', { invokeMethodAsync: () => Promise.resolve() }, { ionToken: 'not-a-real-token' });
                    return null;
                } catch (e) { return String(e); }
            }
            """);
        error.Should().BeNull();

        var status = await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', true)");

        status.Should().StartWith("failed");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', false)")).Should().Be("off");
    }

    [Fact]
    public async Task DriveMode_ShouldReportGpsFixesToThePage_UntilStopped()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await page.Context.GrantPermissionsAsync(["geolocation"], new() { Origin = "http://localhost" });
        await page.Context.SetGeolocationAsync(new() { Latitude = 33.75f, Longitude = -84.39f, Accuracy = 12 });
        (await InitAsync(page)).Should().BeNull();

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.startDrive('tg-viewport')")).Should().Be("started");
        await page.WaitForFunctionAsync("window.__invokedMethods.some(c => c.methodName === 'OnDrivePosition')",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        var args = await page.EvaluateAsync<double[]>("window.__invokedMethods.find(c => c.methodName === 'OnDrivePosition').args.slice(0, 3)");
        args[0].Should().BeApproximately(33.75, 0.001);
        args[1].Should().BeApproximately(-84.39, 0.001);
        args[2].Should().BeApproximately(12, 0.5);

        await page.EvaluateAsync("() => { window.tacticalGlobe.setDriveFollow('tg-viewport', false); window.tacticalGlobe.stopDrive('tg-viewport'); window.tacticalGlobe.stopDrive('tg-viewport'); }");
        var before = await page.EvaluateAsync<int>("window.__invokedMethods.filter(c => c.methodName === 'OnDrivePosition').length");
        await page.Context.SetGeolocationAsync(new() { Latitude = 33.80f, Longitude = -84.39f, Accuracy = 12 });
        await page.WaitForTimeoutAsync(2500);
        (await page.EvaluateAsync<int>("window.__invokedMethods.filter(c => c.methodName === 'OnDrivePosition').length")).Should().Be(before, "stopping clears the GPS watch");
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task TimeMachine_SetReplaceAndClear_ShouldNotThrowAgainstTheRealCesiumBuild()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        // A data: tile keeps the radar layer offline; set twice to exercise the replace path.
        var error = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    const snapshot = {
                        radarTileUrl: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
                        incidents: [{ lat: 33.75, lon: -84.39, category: 'crash', title: 'Crash - I 75' }],
                        earthquakes: [{ lat: 36.1, lon: -97.5, title: 'M 3.1 earthquake' }]
                    };
                    window.tacticalGlobe.setTimeMachine('tg-viewport', snapshot);
                    window.tacticalGlobe.setTimeMachine('tg-viewport', { ...snapshot, radarTileUrl: null });
                    window.tacticalGlobe.clearTimeMachine('tg-viewport');
                    window.tacticalGlobe.clearTimeMachine('tg-viewport');
                    return null;
                } catch (e) { return String(e && e.stack || e); }
            }
            """);
        await page.WaitForTimeoutAsync(500);

        error.Should().BeNull();
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ShareText_ShouldUseTheShareSheetWhenThereIsOne_AndOtherwiseCopy()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;

        var copied = await page.EvaluateAsync<string[]>("""
            async () => {
              Object.defineProperty(navigator, 'share', { value: undefined, configurable: true });
              Object.defineProperty(navigator, 'clipboard', { value: { writeText: text => { window.__copied = text; return Promise.resolve(); } }, configurable: true });
              const result = await window.tacticalGlobe.shareText('Directions', 'Turn left', 'https://example.test/r');
              return [result, window.__copied];
            }
            """);
        copied.Should().Equal("copied", "Turn left\n\nhttps://example.test/r");

        var shared = await page.EvaluateAsync<string[]>("""
            async () => {
              Object.defineProperty(navigator, 'share', { value: data => { window.__shared = data.title + '|' + data.url; return Promise.resolve(); }, configurable: true });
              const result = await window.tacticalGlobe.shareText('Directions', 'Turn left', 'https://example.test/r');
              return [result, window.__shared];
            }
            """);
        shared.Should().Equal("shared", "Directions|https://example.test/r");
    }

    [Fact]
    public async Task Init_ShouldSucceed_AgainstTheRealPinnedCesiumBuildAndTheRealCsp()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;

        var error = await InitAsync(page);
        await page.WaitForTimeoutAsync(500);

        error.Should().BeNull("tacticalGlobe.init() must not throw against the real Cesium build and CSP this app serves in production");
        consoleErrors.Should().NotContain(
            msg => msg.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase),
            "a CSP violation here means the real deployed page would fail exactly the same way, even if init() itself didn't throw");
    }

    [Fact]
    public async Task AfterInit_EveryExportedFunction_ShouldRunWithoutThrowing()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        var result = await page.EvaluateAsync<string?>("""
            async () => {
              try {
                window.tacticalGlobe.setCameraPins([{
                  id: 'a', name: 'Test Camera', lat: 47.6, lon: -122.3,
                  streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                  sourceName: 'WSDOT', sourceAttributionUrl: 'https://wsdot.wa.gov'
                }]);
                window.tacticalGlobe.setRadarVisible(true);
                window.tacticalGlobe.setRadarTimeline({ nowIndex: 0, modelRunUtc: null, frames: [
                  { validUtc: new Date().toISOString(), kind: 'Observed', tileUrlTemplate: 'https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/nexrad-n0q-900913/{z}/{x}/{y}.png' }] });
                window.tacticalGlobe.setHourlyForecast([]);
                window.tacticalGlobe.setMessageSigns([{ id: 's1', name: 'Test sign', lat: 34.05, lon: -118.25, road: 'I-405', direction: 'North',
                  pages: [['CRASH AHEAD', 'LEFT LANE CLOSED']], sourceName: 'Caltrans', sourceAttributionUrl: 'https://quickmap.dot.ca.gov' }]);
                window.tacticalGlobe.clearMessageSigns();
                window.tacticalGlobe.setRoute([[-83.9, 32.84], [-83.4, 32.84]], 'tg-viewport', []);
                window.tacticalGlobe.setRouteHighlights('tg-viewport', [{ kind: 'SunGlare', path: [[-83.9, 32.84], [-83.6, 32.84]] }, { kind: 'Dark', path: [[-83.6, 32.84], [-83.4, 32.84]] }]);
                window.tacticalGlobe.setRouteConditions('tg-viewport', [{ lat: 32.84, lon: -83.6, level: 'bad' }, { lat: 32.84, lon: -83.5, level: 'ok' }]);
                window.tacticalGlobe.setRoadWeather([{ id: 'r1', name: 'Savage', lat: 47.4, lon: -104.4, pavementF: 31, airF: 36, dewpointF: 30,
                  precipitation: 'Light Snow', observedAt: 'Oct 9, 9:00 AM', risk: 'ice-likely', reason: 'pavement 31F with light snow',
                  sourceName: '511MT', sourceAttributionUrl: 'https://www.511mt.net' }]);
                window.tacticalGlobe.clearRoadWeather();
                window.tacticalGlobe.clearRoute('tg-viewport');
                window.tacticalGlobe.setWeatherAlerts([{
                  id: 'alert-1', eventName: 'Severe Thunderstorm Warning', severity: 'Severe', areaDescription: 'Test Area',
                  rings: [[[-122.5, 47.5], [-122.0, 47.5], [-122.0, 48.0], [-122.5, 48.0], [-122.5, 47.5]]]
                }]);
                window.tacticalGlobe.clearWeatherAlerts();
                window.tacticalGlobe.setCameraAlertHighlights([{ cameraId: 'a', severity: 'Severe' }]);
                window.tacticalGlobe.getCameraAlertSeverity('tg-viewport', 'a');
                window.tacticalGlobe.setTrafficIncidents([{
                  id: 'incident-1', roadwayName: 'SR 101', description: 'Crash blocking left lane.',
                  eventType: 'accidentsAndIncidents', severity: 'minor', lat: 47.6, lon: -122.3,
                  sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
                }]);
                window.tacticalGlobe.clearTrafficIncidents();
                window.tacticalGlobe.setWeatherSnapshot({
                  currentTemperatureFahrenheit: 82.4, currentConditions: 'Mostly Clear',
                  forecastTemperatureFahrenheit: 90, shortForecast: 'Chance Showers',
                  detailedForecast: 'A chance of showers.', windSpeed: '5 mph', windDirection: 'E',
                  chanceOfPrecipitationPercent: 50
                });
                window.tacticalGlobe.setWeatherSnapshot(null);
                window.tacticalGlobe.setRadarVisible(false);
                window.tacticalGlobe.flyTo('tg-viewport', 33.75, -84.39, 400000);
                window.tacticalGlobe.setSelectModeEnabled('tg-viewport', true);
                window.tacticalGlobe.setSelectModeEnabled('tg-viewport', false);
                window.tacticalGlobe.openCamera({
                  id: 'cam-x', name: 'Test Camera', lat: 47.6, lon: -122.3,
                  streamUrl: 'https://example.test/x.jpg', streamKind: 'Snapshot',
                  sourceName: 'WSDOT', sourceAttributionUrl: 'https://wsdot.wa.gov'
                });
                window.tacticalGlobe.openWatchWallWithCameras('tg-viewport', [{
                  id: 'cam-y', name: 'Test Camera 2', lat: 47.6, lon: -122.3,
                  streamUrl: 'https://example.test/y.jpg', streamKind: 'Snapshot',
                  sourceName: 'WSDOT', sourceAttributionUrl: 'https://wsdot.wa.gov'
                }]);
                window.tacticalGlobe.closeWatchWall('tg-viewport');
                window.tacticalGlobe.openWatchWall('tg-viewport');
                await window.tacticalGlobe.buildShareLink('tg-viewport');
                window.tacticalGlobe.dispose('tg-viewport');
                return null;
              } catch (e) {
                return e.message;
              }
            }
            """);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PinCluster_Click_ShouldZoomIn_RatherThanOpenAStreamPanel()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        // Three pins a hundredth of a degree apart, at the default camera's look-at point, land
        // on nearly the same screen pixel from the initial whole-continent view - well within
        // clustering.pixelRange (60px) and at/above minimumClusterSize (3), so Cesium groups them
        // into one cluster marker instead of three separate camera pins.
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([
                { id: 'cam-1', name: 'Camera 1', lat: 39.80, lon: -98.50, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' },
                { id: 'cam-2', name: 'Camera 2', lat: 39.81, lon: -98.51, streamUrl: 'https://example.test/b.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' },
                { id: 'cam-3', name: 'Camera 3', lat: 39.79, lon: -98.49, streamUrl: 'https://example.test/c.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' }
              ]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        // flyToBoundingSphere's own animation, not a stream panel appearing, is the expected
        // effect of this click - give it time to run, then assert no panel ever showed up.
        await page.WaitForTimeoutAsync(1500);

        var hasStreamPanel = await page.EvaluateAsync<bool>("!!document.querySelector('.tg-stream-panel')");
        hasStreamPanel.Should().BeFalse(
            "clicking a clustered group of pins should zoom the camera in, not open any single camera's stream panel");
    }

    [Fact]
    public async Task SetCameraAlertHighlights_ShouldHighlightOnlyTheMatchingCamera()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([
                { id: 'cam-1', name: 'Camera 1', lat: 39.80, lon: -98.50, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' },
                { id: 'cam-2', name: 'Camera 2', lat: 34.05, lon: -84.29, streamUrl: 'https://example.test/b.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' }
              ]);
              window.tacticalGlobe.setCameraAlertHighlights([{ cameraId: 'cam-1', severity: 'Severe' }]);
            }
            """);

        var highlighted = await page.EvaluateAsync<string?>(
            "() => window.tacticalGlobe.getCameraAlertSeverity('tg-viewport', 'cam-1')");
        var unhighlighted = await page.EvaluateAsync<string?>(
            "() => window.tacticalGlobe.getCameraAlertSeverity('tg-viewport', 'cam-2')");

        highlighted.Should().Be("Severe");
        unhighlighted.Should().BeNull("only cam-1 was included in the highlights payload");
    }

    [Fact]
    public async Task SelectingAHighlightedPin_ShouldKeepTheAlertHighlight()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([
                { id: 'cam-1', name: 'Camera 1', lat: 39.80, lon: -98.50, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' }
              ]);
              window.tacticalGlobe.setCameraAlertHighlights([{ cameraId: 'cam-1', severity: 'Extreme' }]);
              window.tacticalGlobe.setSelectModeEnabled('tg-viewport', true);
            }
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-selection-indicator')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var severity = await page.EvaluateAsync<string?>(
            "() => window.tacticalGlobe.getCameraAlertSeverity('tg-viewport', 'cam-1')");
        severity.Should().Be("Extreme", "selecting a pin (watch-wall mode) must not clear its independent alert highlight");
    }

    [Fact]
    public async Task ClearWeatherAlerts_ShouldResetEveryCameraHighlight()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([
                { id: 'cam-1', name: 'Camera 1', lat: 39.80, lon: -98.50, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' }
              ]);
              window.tacticalGlobe.setCameraAlertHighlights([{ cameraId: 'cam-1', severity: 'Moderate' }]);
              window.tacticalGlobe.clearWeatherAlerts();
            }
            """);

        var severity = await page.EvaluateAsync<string?>(
            "() => window.tacticalGlobe.getCameraAlertSeverity('tg-viewport', 'cam-1')");
        severity.Should().BeNull("turning ALERTS off must clear every camera pin's highlight, not just the alert polygons");
    }

    [Fact]
    public async Task TrafficIncidentClick_ShouldOpenIncidentPanel_WithDescriptionAndType()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        // Placed exactly at the default camera's look-at point (-98.5, 39.8), so it lands dead
        // center of the canvas without needing to compute a screen projection - same technique
        // already established for camera pins.
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setTrafficIncidents([{
                id: 'incident-1', roadwayName: 'I-85 Northbound', description: 'Multi-vehicle crash, right lane blocked.',
                eventType: 'accidentsAndIncidents', severity: 'minor', lat: 39.8, lon: -98.5,
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-incident-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var panelText = await page.EvaluateAsync<string>("document.querySelector('.tg-incident-panel').textContent");
        panelText.Should().Contain("I-85 Northbound").And.Contain("Multi-vehicle crash").And.Contain("ACCIDENT");
    }

    // Render-on-demand (30-10-26): an idle globe draws about once a second instead of every frame,
    // and a SURGING marker's pulse keeps it drawing only while one is on the globe.
    [Fact]
    public async Task IdleGlobe_ShouldDrawOnlyOnChanges_AndKeepDrawingWhileANewsMarkerPulses()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await page.EvaluateAsync("""
            () => {
              // Cesium's exports are read-only, but its prototypes aren't; render() runs every
              // animation frame and postRender only when a frame is actually drawn.
              window.__frames = 0;
              const render = Cesium.Scene.prototype.render;
              Cesium.Scene.prototype.render = function (time) {
                if (!this.__counted) {
                  this.__counted = true;
                  this.postRender.addEventListener(function () { window.__frames++; });
                }
                return render.call(this, time);
              };
            }
            """);
        await InitAsync(page);
        await page.WaitForTimeoutAsync(1500);

        async Task<int> FramesOverAsync(int milliseconds)
        {
            var before = await page.EvaluateAsync<int>("() => window.__frames");
            await page.WaitForTimeoutAsync(milliseconds);
            return await page.EvaluateAsync<int>("() => window.__frames") - before;
        }

        (await page.EvaluateAsync<int>("() => window.__frames")).Should().BePositive("the count has to see frames at all");
        (await FramesOverAsync(3000)).Should().BeLessThan(15, "nothing changed, so only the once-a-second frame draws");

        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setWorldNews([{
              id: 'situation-1', title: 'United States rail disruption', latest: 'Services begin to recover',
              country: 'United States', lat: 39.8, lon: -98.5, eventCount: 3, outletCount: 12,
              reportCount: 18, lastSeen: '29-10-26 10:30', surge: 'Coverage doubled in 2 hours',
              href: '/admin/world-news?situation=situation-1', sources: []
            }])
            """);
        (await FramesOverAsync(1500)).Should().BeGreaterThan(8, "the SURGING pulse needs frames while it's there");

        await page.EvaluateAsync("() => window.tacticalGlobe.clearWorldNews()");
        await page.WaitForTimeoutAsync(300);
        (await FramesOverAsync(3000)).Should().BeLessThan(15, "the pulse stopped with the marker");
    }

    [Fact]
    public async Task WorldNewsClick_ShouldOpenReportedNewsPanel_WithSourcesAndDeepLink()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setWorldNews([{
              id: 'situation-1', title: 'United States rail disruption', latest: 'Services begin to recover',
              country: 'United States', lat: 39.8, lon: -98.5, eventCount: 3, outletCount: 12,
              reportCount: 18, lastSeen: '29-10-26 10:30', summary: 'Several operators reported delays.',
              href: '/admin/world-news?situation=situation-1',
              sources: [{ outlet: 'Example News', title: 'Rail services delayed', url: 'https://example.test/report' }]
            }])
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-incident-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var panelText = await page.EvaluateAsync<string>("document.querySelector('.tg-incident-panel').textContent");
        panelText.Should().Contain("REPORTED NEWS").And.Contain("NOT A CONFIRMED INCIDENT")
            .And.Contain("United States rail disruption").And.Contain("12 OUTLETS");
        (await page.GetAttributeAsync(".tg-incident-panel a.tg-incident-report", "href"))
            .Should().Be("/admin/world-news?situation=situation-1");
    }

    [Fact]
    public async Task OpenWorldNews_ShouldOpenThePanelWithoutAClick_ForWorldNewsLinks()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => window.tacticalGlobe.openWorldNews({
              id: 'situation-2', title: 'Japan earthquake aftershocks', latest: 'Japan earthquake aftershocks',
              country: 'Japan', place: 'Sendai, Japan', precision: 'city', kindLabel: 'Disasters', active: false,
              surge: 'Surging: coverage went from 3 to 8 outlets in the last 2 hours.',
              lat: 38.3, lon: 140.9, eventCount: 2, outletCount: 7, reportCount: 9,
              lastSeen: '29-10-26 11:05', summary: null, href: '/admin/world-news?situation=situation-2', sources: []
            })
            """);

        await page.WaitForFunctionAsync("!!document.querySelector('.tg-incident-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });
        var panelText = await page.EvaluateAsync<string>("document.querySelector('.tg-incident-panel').textContent");
        panelText.Should().Contain("Japan earthquake aftershocks").And.Contain("7 OUTLETS").And.NotContain("LATEST:")
            .And.Contain("CITY-LEVEL PLACEMENT").And.Contain("DISASTERS").And.Contain("QUIET").And.Contain("Sendai, Japan")
            .And.Contain("Surging: coverage went from 3 to 8 outlets");
        (await page.GetAttributeAsync(".tg-incident-panel a.tg-incident-report", "href"))
            .Should().Be("/admin/world-news?situation=situation-2");
    }

    [Fact]
    public async Task WorldNewsPanel_ShouldShowBackingAndNearby_PlayTheStory_AndDrawWhoIsReporting()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.__mockInvoke = {
                WorldNewsContextAsync: { backing: [{ source: 'USGS', text: 'M 5.4 - 12 km NE of Tokyo, Japan', url: null }],
                  precise: true, cameras: [], alerts: ['Flood Warning'], hazards: [], outages: [] },
                WorldNewsTimeline: [
                  { title: 'Quake shakes Tokyo', at: '28-10-26 09:00', outlets: 4, place: 'Tokyo, Japan', lat: 35.7, lon: 139.7 },
                  { title: 'Aftershocks hit Sendai', at: '29-10-26 06:30', outlets: 9, place: 'Sendai, Japan', lat: 38.3, lon: 140.9 }
                ],
                WorldNewsCoverage: [{ country: 'Japan', lat: 36.1, lon: 138.4, outlets: 5 }, { country: 'United Kingdom', lat: 54, lon: -2, outlets: 2 }],
                CaptureWorldNewsAsync: 'Recording 2 cameras for an hour. See AREAS > CAPTURES.'
              };
              window.tacticalGlobe.openWorldNews({
                id: 'quake', title: 'M5.4 quake shakes Tokyo', latest: 'M5.4 quake shakes Tokyo', country: 'Japan', place: 'Tokyo, Japan',
                precision: 'city', lat: 35.7, lon: 139.7, eventCount: 2, outletCount: 9, reportCount: 12, lastSeen: '29-10-26 06:30',
                summary: null, href: '/admin/world-news?situation=quake', sources: []
              });
            }
            """);

        await page.WaitForFunctionAsync("document.querySelector('.tg-world-news-context')?.textContent.includes('BACKED BY')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
        var context = await page.TextContentAsync(".tg-world-news-context");
        context.Should().Contain("USGS: M 5.4 - 12 km NE of Tokyo, Japan").And.Contain("WEATHER ALERTS").And.Contain("Flood Warning");

        await page.ClickAsync(".tg-world-news-play-button");
        await page.WaitForFunctionAsync("document.querySelector('.tg-world-news-step')?.textContent.includes('EVENT 1 OF 2')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
        await page.EvaluateAsync("""
            () => {
              const range = document.querySelector('.tg-world-news-playback input[type=range]');
              range.value = '1';
              range.dispatchEvent(new Event('input'));
            }
            """);
        (await page.TextContentAsync(".tg-world-news-step")).Should().Contain("EVENT 2 OF 2").And.Contain("Sendai, Japan");

        await page.ClickAsync(".tg-world-news-coverage-button");
        await page.WaitForFunctionAsync("document.querySelector('.tg-world-news-coverage')?.textContent.includes('Japan 5')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
        (await page.EvaluateAsync<bool>("document.querySelector('.tg-modal-backdrop').classList.contains('tg-modal-backdrop-clear')"))
            .Should().BeTrue("the lines are drawn on the globe behind the panel");

        await page.ClickAsync(".tg-world-news-context .tg-world-news-capture");
        await page.WaitForFunctionAsync("document.querySelector('.tg-world-news-context .tg-world-news-capture').textContent.startsWith('RECORDING 2 CAMERAS')",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });
    }

    [Fact]
    public async Task WeatherSnapshot_ShouldRenderCurrentConditionsInThePanel()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setWeatherSnapshot({
                currentTemperatureFahrenheit: 82.4, currentConditions: 'Mostly Clear',
                forecastTemperatureFahrenheit: 90, shortForecast: 'Chance Showers And Thunderstorms',
                detailedForecast: 'A chance of showers.', windSpeed: '5 mph', windDirection: 'E',
                chanceOfPrecipitationPercent: 50
              });
            }
            """);
        await page.WaitForFunctionAsync("document.querySelector('.tg-weather-panel') && document.querySelector('.tg-weather-panel').style.display === 'block'", new PageWaitForFunctionOptions { Timeout = 5000 });

        var panelText = await page.EvaluateAsync<string>("document.querySelector('.tg-weather-panel').textContent");
        panelText.Should().Contain("82").And.Contain("Mostly Clear").And.Contain("Chance Showers");
    }

    [Fact]
    public async Task WeatherSnapshot_ShouldHideThePanel_WhenGivenNull()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("() => { window.tacticalGlobe.setWeatherSnapshot({ currentTemperatureFahrenheit: 70, currentConditions: 'Clear', forecastTemperatureFahrenheit: 70, shortForecast: 'Clear', detailedForecast: 'Clear', windSpeed: '0 mph', windDirection: '', chanceOfPrecipitationPercent: null }); }");
        await page.WaitForFunctionAsync("document.querySelector('.tg-weather-panel').style.display === 'block'", new PageWaitForFunctionOptions { Timeout = 5000 });

        await page.EvaluateAsync("() => { window.tacticalGlobe.setWeatherSnapshot(null); }");
        var display = await page.EvaluateAsync<string>("document.querySelector('.tg-weather-panel').style.display");
        display.Should().Be("none");
    }

    // Two past frames, the latest scan, and two HRRR future frames - real IEM tile templates, so
    // the test also proves the browser can actually fetch radar tiles under the real CSP.
    private const string RadarTimelineScript = """
        () => {
          const now = Date.now();
          const iso = (minutes) => new Date(now + minutes * 60000).toISOString();
          const base = 'https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/';
          window.tacticalGlobe.setRadarTimeline({
            nowIndex: 2,
            modelRunUtc: iso(-150),
            frames: [
              { validUtc: iso(-10), kind: 'Observed', tileUrlTemplate: base + 'nexrad-n0q-900913-m10m/{z}/{x}/{y}.png' },
              { validUtc: iso(-5), kind: 'Observed', tileUrlTemplate: base + 'nexrad-n0q-900913-m05m/{z}/{x}/{y}.png' },
              { validUtc: iso(0), kind: 'Observed', tileUrlTemplate: base + 'nexrad-n0q-900913/{z}/{x}/{y}.png' },
              { validUtc: iso(60), kind: 'Forecast', tileUrlTemplate: base + 'hrrr::REFD-F0060-0/{z}/{x}/{y}.png' },
              { validUtc: iso(120), kind: 'Forecast', tileUrlTemplate: base + 'hrrr::REFD-F0120-0/{z}/{x}/{y}.png' }
            ]
          });
          window.tacticalGlobe.setHourlyForecast([
            { startUtc: iso(30), endUtc: iso(90), temperatureFahrenheit: 71.6, chanceOfPrecipitationPercent: 60, shortForecast: 'Showers Likely' }
          ]);
        }
        """;

    [Fact]
    public async Task RadarTimeline_ShouldOpenOnTheLatestScan_LoadRealTilesUnderTheCsp_AndStepIntoTheForecast()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        var radarTileResponses = new List<int>();
        page.Response += (_, response) =>
        {
            if (response.Url.StartsWith("https://mesonet.agron.iastate.edu/", StringComparison.Ordinal))
            {
                lock (radarTileResponses) radarTileResponses.Add(response.Status);
            }
        };

        await page.EvaluateAsync(RadarTimelineScript);
        await page.EvaluateAsync("() => window.tacticalGlobe.setRadarVisible(true)");
        await page.WaitForFunctionAsync("document.querySelector('.tg-radar-bar') && document.querySelector('.tg-radar-bar').style.display === 'block'", new PageWaitForFunctionOptions { Timeout = 5000 });

        (await page.TextContentAsync(".tg-radar-kind")).Should().Be("LATEST RADAR");
        (await page.InputValueAsync(".tg-radar-slider")).Should().Be("2");

        await page.ClickAsync("[data-radar='forward']");
        (await page.TextContentAsync(".tg-radar-kind")).Should().StartWith("FUTURE RADAR · HRRR MODEL");
        (await page.InputValueAsync(".tg-radar-slider")).Should().Be("3");

        await page.ClickAsync("[data-radar='now']");
        (await page.InputValueAsync(".tg-radar-slider")).Should().Be("2");

        await page.ClickAsync("[data-radar='play']");
        (await page.GetAttributeAsync(".tg-radar-play", "aria-label")).Should().Be("Pause radar loop");
        await page.ClickAsync("[data-radar='play']");
        (await page.GetAttributeAsync(".tg-radar-play", "aria-label")).Should().Be("Play radar loop");

        await page.WaitForTimeoutAsync(4000);
        lock (radarTileResponses) radarTileResponses.Should().Contain(200, "the globe should actually fetch radar tiles from IEM");
        consoleErrors.Should().NotContain(msg => msg.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase));

        await page.EvaluateAsync("() => window.tacticalGlobe.setRadarVisible(false)");
        (await page.EvaluateAsync<string>("document.querySelector('.tg-radar-bar').style.display")).Should().Be("none");
    }

    [Fact]
    public async Task RadarTimeline_ShouldShowTheHourlyForecast_ForTheSelectedFutureFrameOnly()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("() => { window.tacticalGlobe.setWeatherSnapshot({ currentTemperatureFahrenheit: 70, currentConditions: 'Clear', forecastTemperatureFahrenheit: 70, shortForecast: 'Clear', detailedForecast: 'Clear', windSpeed: '5 mph', windDirection: 'E', chanceOfPrecipitationPercent: 10 }); }");
        await page.EvaluateAsync(RadarTimelineScript);
        await page.EvaluateAsync("() => window.tacticalGlobe.setRadarVisible(true)");

        (await page.EvaluateAsync<string>("document.querySelector('.tg-weather-at').style.display")).Should().Be("none",
            "the latest scan is 'now' - the panel's main lines already cover it");

        await page.ClickAsync("[data-radar='forward']");
        var line = await page.TextContentAsync(".tg-weather-at");
        line.Should().StartWith("AT ").And.Contain("72°F").And.Contain("60% precip").And.Contain("Showers Likely");

        await page.ClickAsync("[data-radar='forward']");
        (await page.EvaluateAsync<string>("document.querySelector('.tg-weather-at').style.display")).Should().Be("none",
            "no hourly period covers that frame's time");
    }

    [Fact]
    public async Task StreamPanel_ShouldOpenAsACenteredModal_InsideABackdrop()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var isInsideBackdrop = await page.EvaluateAsync<bool>("!!document.querySelector('.tg-modal-backdrop .tg-stream-panel')");
        isInsideBackdrop.Should().BeTrue("a single camera's feed opens inside a dark backdrop overlay, centered on open");
    }

    [Fact]
    public async Task StreamPanel_ShouldBeDraggableAndResizable()
    {
        // The modal opens centered but stays fully interactive - drag by the header, resize via
        // the corner handle - same as the pre-modal floating-panel design and the watch wall
        // below, per explicit user request after the backdrop/centering conversion shipped.
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var beforeTop = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().top");
        var headerBox = await page.EvaluateAsync<double[]>("""
            () => {
              const r = document.querySelector('.tg-stream-panel-header').getBoundingClientRect();
              return [r.left + r.width / 2, r.top + r.height / 2];
            }
            """);
        await page.Mouse.MoveAsync((float)headerBox[0], (float)headerBox[1]);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(headerBox[0] + 30), (float)(headerBox[1] + 70), new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();
        var afterTop = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().top");
        (afterTop - beforeTop).Should().BeApproximately(70, 5, "dragging the header should move the modal panel");

        var beforeWidth = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().width");
        var handleBox = await page.EvaluateAsync<double[]>("""
            () => {
              const r = document.querySelector('.tg-stream-panel .tg-panel-resize-handle').getBoundingClientRect();
              return [r.left + r.width / 2, r.top + r.height / 2];
            }
            """);
        await page.Mouse.MoveAsync((float)handleBox[0], (float)handleBox[1]);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(handleBox[0] + 60), (float)(handleBox[1] + 40), new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();
        var afterWidth = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().width");
        (afterWidth - beforeWidth).Should().BeApproximately(60, 5, "dragging the resize handle should widen the modal panel");
    }

    [Fact]
    public async Task StreamPanel_ShouldNotDismiss_WhenTheBackdropReceivesTheSecondClickOfADoubleClick()
    {
        // Regression test for a real reported bug: the modal is centered on the shell, not at
        // wherever the user actually clicked to open it (a camera/incident pin is almost never
        // exactly at the shell's center) - so double-clicking a pin opened the modal on the first
        // click and immediately dismissed it on the second, since that second click's mousedown
        // landed on the backdrop (which covers everywhere except the centered panel), not the
        // panel itself. Dispatching a synthetic mousedown with detail:2 directly on the backdrop
        // (rather than relying on exact off-center pin/camera-projection geometry) is a more
        // precise way to test the fix (openAsModal's event.detail <= 1 guard) than orchestrating
        // a real double-click on a pin that happens to not be at the harness's screen center.
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        await page.EvaluateAsync("""
            () => {
              const backdrop = document.querySelector('.tg-modal-backdrop');
              backdrop.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, detail: 2 }));
            }
            """);
        await page.WaitForTimeoutAsync(200);

        (await page.EvaluateAsync<bool>("!!document.querySelector('.tg-stream-panel')")).Should()
            .BeTrue("the second click of a double-click on the backdrop must not dismiss a modal that same gesture just opened");
    }

    [Fact]
    public async Task StreamPanel_ShouldCloseWhenClickingTheBackdrop_ButNotWhenClickingInsideIt()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        // Clicking inside the centered panel itself must not close it.
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        (await page.EvaluateAsync<bool>("!!document.querySelector('.tg-stream-panel')")).Should().BeTrue();

        // Clicking the backdrop, well outside the centered panel, closes it.
        await page.Mouse.MoveAsync(20, 20);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });
    }

    [Fact]
    public async Task SelectMode_ShouldAddToSelectionInsteadOfOpeningSinglePanel_WhenEnabled()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
              window.tacticalGlobe.setSelectModeEnabled('tg-viewport', true);
            }
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-selection-indicator')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var hasStreamPanel = await page.EvaluateAsync<bool>("!!document.querySelector('.tg-stream-panel')");
        hasStreamPanel.Should().BeFalse("while select mode is on, clicking a camera pin should add it to the selection, not open its stream panel");

        var indicatorText = await page.EvaluateAsync<string>("document.querySelector('.tg-selection-indicator').textContent");
        indicatorText.Should().Contain("1");
    }

    [Fact]
    public async Task WatchWall_ShouldRenderOneTilePerSelectedCamera()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.openWatchWallWithCameras('tg-viewport', [
                { id: 'cam-1', name: 'Camera 1', lat: 39.8, lon: -98.5, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' },
                { id: 'cam-2', name: 'Camera 2', lat: 47.6, lon: -122.3, streamUrl: 'https://example.test/b.jpg', streamKind: 'Snapshot', sourceName: 'WSDOT', sourceAttributionUrl: 'https://wsdot.wa.gov' }
              ]);
            }
            """);
        await page.WaitForFunctionAsync("document.querySelectorAll('.tg-watch-wall-tile').length === 2", new PageWaitForFunctionOptions { Timeout = 5000 });

        var tileNames = await page.EvaluateAsync<string[]>(
            "Array.from(document.querySelectorAll('.tg-watch-wall-tile-header span')).map(el => el.textContent)");
        tileNames.Should().Contain("Camera 1").And.Contain("Camera 2");
    }

    [Fact]
    public async Task WatchWall_ShouldBeDraggableAndResizable()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.openWatchWallWithCameras('tg-viewport', [
                { id: 'cam-1', name: 'Camera 1', lat: 39.8, lon: -98.5, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' }
              ]);
            }
            """);
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-watch-wall-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var beforeTop = await page.EvaluateAsync<double>("document.querySelector('.tg-watch-wall-panel').getBoundingClientRect().top");
        var headerBox = await page.EvaluateAsync<double[]>("""
            () => {
              const r = document.querySelector('.tg-watch-wall-header').getBoundingClientRect();
              return [r.left + r.width / 2, r.top + r.height / 2];
            }
            """);
        await page.Mouse.MoveAsync((float)headerBox[0], (float)headerBox[1]);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(headerBox[0] + 40), (float)(headerBox[1] + 90), new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();
        var afterTop = await page.EvaluateAsync<double>("document.querySelector('.tg-watch-wall-panel').getBoundingClientRect().top");
        (afterTop - beforeTop).Should().BeApproximately(90, 5, "dragging the header should move the watch wall panel");

        var beforeWidth = await page.EvaluateAsync<double>("document.querySelector('.tg-watch-wall-panel').getBoundingClientRect().width");
        var handleBox = await page.EvaluateAsync<double[]>("""
            () => {
              const r = document.querySelector('.tg-watch-wall-panel .tg-panel-resize-handle').getBoundingClientRect();
              return [r.left + r.width / 2, r.top + r.height / 2];
            }
            """);
        await page.Mouse.MoveAsync((float)handleBox[0], (float)handleBox[1]);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(handleBox[0] + 80), (float)(handleBox[1] + 50), new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();
        var afterWidth = await page.EvaluateAsync<double>("document.querySelector('.tg-watch-wall-panel').getBoundingClientRect().width");
        (afterWidth - beforeWidth).Should().BeApproximately(80, 5, "dragging the resize handle should widen the watch wall panel");
    }

    [Fact]
    public async Task WatchWall_ClosingThenClickingACamera_ShouldStillOpenANormalStreamPanel()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);

        // Regression guard on the renderStream singleton -> per-panel dispose refactor: using a
        // watch wall (which calls renderStream/dispose through its own tiles) must not leave any
        // shared state behind that breaks a normal single-camera stream panel afterward.
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.openWatchWallWithCameras('tg-viewport', [
                { id: 'cam-1', name: 'Camera 1', lat: 39.8, lon: -98.5, streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org' }
              ]);
              window.tacticalGlobe.closeWatchWall('tg-viewport');
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-2', name: 'Camera 2', lat: 39.8, lon: -98.5, streamUrl: 'https://example.test/b.jpg', streamKind: 'Snapshot', sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);

        var wallStillPresent = await page.EvaluateAsync<bool>("!!document.querySelector('.tg-watch-wall-panel')");
        wallStillPresent.Should().BeFalse("closeWatchWall should fully remove the panel from the DOM");

        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var panelText = await page.EvaluateAsync<string>("document.querySelector('.tg-stream-panel').textContent");
        panelText.Should().Contain("Camera 2");
    }

    [Fact]
    public async Task FavoriteStar_ShouldInvokeToggleCameraFavoriteAsync_AndUpdateItsVisualState()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("() => { window.__mockToggleFavoriteResult = true; }");
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org', isFavorite: false
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel-favorite')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var beforePressed = await page.EvaluateAsync<string>("document.querySelector('.tg-stream-panel-favorite').getAttribute('aria-pressed')");
        beforePressed.Should().Be("false");

        await page.ClickAsync(".tg-stream-panel-favorite");
        await page.WaitForFunctionAsync("document.querySelector('.tg-stream-panel-favorite').getAttribute('aria-pressed') === 'true'", new PageWaitForFunctionOptions { Timeout = 5000 });

        var invoked = await page.EvaluateAsync<bool>(
            "window.__invokedMethods.some(c => c.methodName === 'ToggleCameraFavoriteAsync' && c.args[0] === 'cam-1')");
        invoked.Should().BeTrue("clicking the star should call ToggleCameraFavoriteAsync with the camera's id");
    }

    [Fact]
    public async Task AnalyzeButton_ShouldInvokeAnalyzeCameraAsync_AndRenderTheReturnedText()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("() => { window.__mockAnalyzeResult = 'light traffic, clear skies'; }");
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org', isFavorite: false
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel-analyze')", new PageWaitForFunctionOptions { Timeout = 5000 });

        await page.ClickAsync(".tg-stream-panel-analyze");
        await page.WaitForFunctionAsync(
            "!!document.querySelector('.tg-stream-panel-analysis') && document.querySelector('.tg-stream-panel-analysis').textContent === 'light traffic, clear skies'",
            new PageWaitForFunctionOptions { Timeout = 5000 });

        var invoked = await page.EvaluateAsync<bool>(
            "window.__invokedMethods.some(c => c.methodName === 'AnalyzeCameraAsync' && c.args[0] === 'cam-1')");
        invoked.Should().BeTrue("clicking ANALYZE should call AnalyzeCameraAsync with the camera's id");
    }

    [Fact]
    public async Task AnalyzeButton_ShouldShowADisabledAnalyzingState_WhileTheCallIsInFlight()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.__mockAnalyzeResult = 'clear skies';
              // Long enough to leave a reliable window to observe the "during" state even on a
              // slower/more contended CI runner - confirmed via real CI failures that a short
              // delay combined with reading DOM state immediately after ClickAsync (with no
              // wait) is a genuine race: the click handler's synchronous disabled/text mutation
              // can still be a tick behind ClickAsync's own return on a loaded runner, even
              // though nothing async happens before those two lines in the handler itself.
              window.__mockAnalyzeDelayMs = 2000;
            }
            """);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org', isFavorite: false
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel-analyze')", new PageWaitForFunctionOptions { Timeout = 5000 });

        await page.ClickAsync(".tg-stream-panel-analyze");

        // Wait for the "during" state rather than reading it immediately after ClickAsync - the
        // click handler's disable/text mutation is synchronous in the app code, but nothing
        // guarantees it has already run on the renderer's main thread by the time Playwright's
        // click() call itself returns, especially on a slower/loaded runner. The 2s mock delay
        // above leaves a wide, safe window for this to become true well before the mocked
        // response resolves and reverts it.
        //
        // Text and disabled are checked in ONE evaluation: reading them in two separate calls
        // let the mocked response land in between on a loaded CI runner (2026-10-10 deploy run
        // 38010579554), which re-enabled the button and failed the test with no app bug.
        var duringBoth = await page.WaitForFunctionAsync(
            "(() => { const b = document.querySelector('.tg-stream-panel-analyze'); return b.textContent === 'ANALYZING...' && b.disabled; })()",
            null,
            new PageWaitForFunctionOptions { Timeout = 5000 });
        (await duringBoth.JsonValueAsync<bool>()).Should().BeTrue();

        await page.WaitForFunctionAsync(
            "!!document.querySelector('.tg-stream-panel-analysis') && document.querySelector('.tg-stream-panel-analysis').textContent === 'clear skies'",
            new PageWaitForFunctionOptions { Timeout = 5000 });
        var afterText = await page.EvaluateAsync<string>("document.querySelector('.tg-stream-panel-analyze').textContent");
        afterText.Should().Be("ANALYZE");
    }

    [Fact]
    public async Task ShareButton_ShouldInvokeBuildShareLinkAsync_WithTheOpenCameraAndCurrentPosition()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("() => { window.__mockShareLink = 'https://example.test/admin/osint?v=abc123'; }");
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setCameraPins([{
                id: 'cam-1', name: 'Test Camera', lat: 39.8, lon: -98.5,
                streamUrl: 'https://example.test/a.jpg', streamKind: 'Snapshot',
                sourceName: 'GDOT', sourceAttributionUrl: 'https://511ga.org'
              }]);
            }
            """);
        await page.WaitForTimeoutAsync(1000);
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-stream-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        await page.ClickAsync("#tg-share-button");
        await page.WaitForFunctionAsync(
            "window.__invokedMethods.some(c => c.methodName === 'BuildShareLinkAsync')",
            new PageWaitForFunctionOptions { Timeout = 5000 });

        var call = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            "window.__invokedMethods.find(c => c.methodName === 'BuildShareLinkAsync')");
        var args = call.GetProperty("args");
        args[0].GetDouble().Should().BeApproximately(39.8, 0.5, "the share link should encode roughly the current camera latitude");
        var openCameras = args[3];
        openCameras.GetArrayLength().Should().Be(1);
        openCameras[0].GetProperty("id").GetString().Should().Be("cam-1");
    }

    [Fact]
    public async Task LocationSearch_ShouldFlyToTheGeocodedResult_OnGoClick()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("() => { window.__mockGeocodeResult = { latitude: 33.7490, longitude: -84.3880 }; }");

        await page.FillAsync("#tg-search-input", "Atlanta, GA");
        await page.ClickAsync("#tg-search-button");
        await page.WaitForFunctionAsync("document.getElementById('tg-search-status').textContent === ''", new PageWaitForFunctionOptions { Timeout = 5000 });

        var status = await page.EvaluateAsync<string>("document.getElementById('tg-search-status').textContent");
        status.Should().BeEmpty("a successful geocode clears the status text rather than leaving a stale message");
    }

    [Fact]
    public async Task LocationSearch_ShouldShowNotFound_WhenGeocodingReturnsNoResult()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("() => { window.__mockGeocodeResult = null; }");

        await page.FillAsync("#tg-search-input", "a place that does not exist anywhere");
        await page.ClickAsync("#tg-search-button");
        await page.WaitForFunctionAsync("document.getElementById('tg-search-status').textContent === 'NOT FOUND'", new PageWaitForFunctionOptions { Timeout = 5000 });
    }

    // Zooms in via real mouse-wheel input (the same gesture a user performs), rather than a
    // direct camera API call - this is what actually exercises setUpHoverIdentify's own height
    // check against the real Cesium camera, not just an assumption about what "zoomed in" means.
    // Places the camera at 1.5 km (below the 5 km hover threshold) instantly via flyTo's
    // zero-duration form. This used to simulate 50 wheel-zoom steps, which was flaky: headless
    // Chromium renders Cesium in software, and when its main thread was busy the wheel events
    // weren't processed - the view stayed at whole-globe and a later wheel call hung 30s.
    private static async Task ZoomInBelowHoverThresholdAsync(IPage page)
    {
        await page.EvaluateAsync("() => window.tacticalGlobe.flyTo('tg-viewport', 38.8977, -77.0365, 1500, 0)");
        await page.Mouse.MoveAsync(400, 300);
    }

    [Fact]
    public async Task HoverIdentify_ShouldShowTooltip_WhenZoomedInAndAPlaceIsFound()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await ZoomInBelowHoverThresholdAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.__mockPlaceInfo = { displayName: 'White House', address: '1600 Pennsylvania Avenue Northwest, Washington, DC', category: 'government' };
            }
            """);

        await page.Mouse.MoveAsync(410, 310);
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-hover-tooltip')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var tooltipText = await page.EvaluateAsync<string>("document.querySelector('.tg-hover-tooltip').textContent");
        tooltipText.Should().Contain("White House").And.Contain("Washington, DC").And.Contain("government");

        var invoked = await page.EvaluateAsync<bool>("window.__invokedMethods.some(m => m.methodName === 'ReverseGeocodeAsync')");
        invoked.Should().BeTrue("hovering while zoomed in should call the real reverse-geocode interop method");
    }

    [Fact]
    public async Task HoverIdentify_ShouldNotCallReverseGeocode_WhenZoomedOut()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        // No zoom - stays at init's own far default view (18,000 km altitude).

        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.MoveAsync(410, 310);
        await page.WaitForTimeoutAsync(800); // past the 400ms debounce, with margin

        var invoked = await page.EvaluateAsync<bool>("window.__invokedMethods.some(m => m.methodName === 'ReverseGeocodeAsync')");
        invoked.Should().BeFalse("identifying a specific building is meaningless (and wasteful) from a whole-continent view");
    }

    // Regression guard for the coverage banner's dismiss state (OverwatchGrid.razor): it's
    // permanently accurate for an admin who never configures the optional WSDOT/Windy keys, so
    // it never clears itself server-side - dismissal has to persist client-side instead.
    [Fact]
    public async Task CoverageBanner_ShouldStayDismissed_AfterDismissAndReload()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;

        var dismissedBeforeInit = await page.EvaluateAsync<bool>("() => window.tacticalGlobe.isCoverageBannerDismissed()");
        dismissedBeforeInit.Should().BeFalse("a fresh browser/profile has never dismissed the banner");

        await page.EvaluateAsync("() => window.tacticalGlobe.dismissCoverageBanner()");
        var dismissedAfterCall = await page.EvaluateAsync<bool>("() => window.tacticalGlobe.isCoverageBannerDismissed()");
        dismissedAfterCall.Should().BeTrue();

        // Simulate a fresh page load (a new admin visit) re-reading the same localStorage key -
        // the dismissal must survive a full script re-evaluation, not just live in memory.
        await page.ReloadAsync();
        await page.WaitForFunctionAsync("!!window.tacticalGlobe", new PageWaitForFunctionOptions { Timeout = 10000 });
        var dismissedAfterReload = await page.EvaluateAsync<bool>("() => window.tacticalGlobe.isCoverageBannerDismissed()");
        dismissedAfterReload.Should().BeTrue("dismissal must persist across page loads, not just within one session");
    }
}
