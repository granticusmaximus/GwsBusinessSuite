using System.Text.Json;
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
[Collection("PlaywrightOverwatchA")]
public sealed class OverwatchGridScriptBrowserTests(PlaywrightBrowserFixture fixture)
{
    // Kept in sync with Program.cs's CSP for the /admin/osint route by hand - there's no shared
    // constant to import across the Web/Test project boundary. If this test ever passes while
    // the real page fails, check this string against Program.cs's script-src first.
    internal const string OverwatchGridCsp =
        "default-src 'self'; " +
        "script-src 'self' https://cdn.jsdelivr.net 'unsafe-eval' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "img-src 'self' data: blob: https:; " +
        "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; " +
        "connect-src 'self' wss: ws: https://nominatim.openstreetmap.org https://*.azurewebsites.net https://cdn.jsdelivr.net https://server.arcgisonline.com https://tile.openstreetmap.org https://*.tile.openstreetmap.org https://mesonet.agron.iastate.edu https://tilecache.rainviewer.com https://gibs.earthdata.nasa.gov; " +
        "media-src 'self' blob: https:; " +
        "worker-src 'self' blob: https://cdn.jsdelivr.net; " +
        "frame-src 'self'; " +
        "frame-ancestors 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';";

    internal static string ScriptPath => Path.GetFullPath(Path.Combine(
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
    internal const string HarnessHtml = """
        <!doctype html>
        <html>
        <head>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/cesium@1.144.0/Build/Cesium/Widgets/widgets.css" />
          <script src="https://cdn.jsdelivr.net/npm/cesium@1.144.0/Build/Cesium/Cesium.js"></script>
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

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<(int Status, Dictionary<string, string> Headers, byte[] Body)>> CesiumFiles = new(StringComparer.Ordinal);

    internal static async Task CesiumFromMemoryAsync(IRoute route)
    {
        var url = route.Request.Url;
        var file = CesiumFiles.GetOrAdd(url, _ => FetchOnceAsync(route));
        try
        {
            var (status, headers, body) = await file;
            await route.FulfillAsync(new() { Status = status, Headers = headers, BodyBytes = body });
        }
        catch (PlaywrightException)
        {
            // A failed first fetch isn't kept: the next test fetches it again.
            CesiumFiles.TryRemove(url, out _);
            await route.ContinueAsync();
        }
    }

    internal static async Task<(int, Dictionary<string, string>, byte[])> FetchOnceAsync(IRoute route)
    {
        var response = await route.FetchAsync();
        var headers = response.Headers
            .Where(header => !header.Key.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
        headers["access-control-allow-origin"] = "*";
        return (response.Status, headers, await response.BodyAsync());
    }

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
    internal static async Task<(IPage Page, List<string> ConsoleErrors)> OpenHarnessAsync(IBrowser browser, bool macApp = false)
    {
        var script = await File.ReadAllTextAsync(ScriptPath);

        var page = await browser.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, msg) => { if (msg.Type == "error") consoleErrors.Add(msg.Text); };
        if (macApp)
        {
            // What the Mac app injects (MacWebViewCompatibility), plus a stand-in for its native
            // message handler that records each message instead of drawing MapKit.
            await page.AddInitScriptAsync("""
                window.__gwsMacCatalystWebView = true;
                window.__nativeMessages = [];
                window.webkit = { messageHandlers: { gwsNativeOverwatch: {
                    postMessage: function (message) { window.__nativeMessages.push(JSON.parse(JSON.stringify(message))); }
                } } };
                """);
        }

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
        // Each test's page is a fresh browser context, so each one downloaded Cesium (4 MB, plus its
        // workers and assets) from the CDN again: most of this class's 170 s (12-11-26). Each file
        // is fetched once per test run and served from memory after that.
        await page.RouteAsync("https://cdn.jsdelivr.net/npm/cesium@1.144.0/**", CesiumFromMemoryAsync);
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
    internal static async Task<string?> InitAsync(IPage page) => await page.EvaluateAsync<string?>("""
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

    // Overwatch's boot screen lifts on this (30-10-26) instead of after a fixed 1.8 s.
    [Fact]
    public async Task WhenDrawn_ShouldResolveOnceTheFirstTilesAreIn_AndAtOnceForAMissingGlobe()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.whenDrawn('nowhere', 5000)")).Should().BeFalse("there's no globe there to wait for");

        (await InitAsync(page)).Should().BeNull();

        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.whenDrawn('tg-viewport', 60000)")).Should().BeTrue();
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
    internal const string FrameTimeScript = """
        () => new Promise(resolve => {
            const start = performance.now();
            let frames = 0;
            const tick = () => { frames++; if (frames === 10) resolve((performance.now() - start) / 10); else requestAnimationFrame(tick); };
            requestAnimationFrame(tick);
        })
        """;

    // World signals (05-11-26): GDACS kinds and IODA outages draw as hazard pins, and a convergence
    // square opens its own window listing what meets there.
    [Fact]
    public async Task WorldSignals_ShouldDrawGdacsKindsOutagesAndConvergence_AndOpenTheConvergenceWindow()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        var error = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    window.tacticalGlobe.setHazards([
                        { id: 'gdacs-TC-1', kind: 'cyclone', title: 'Tropical Cyclone A', detail: '', severity: 'major', url: 'https://www.gdacs.org', lat: 15.8, lon: 80.2 },
                        { id: 'gdacs-VO-1', kind: 'volcano', title: 'Eruption Taal', detail: '', severity: 'moderate', url: 'https://www.gdacs.org', lat: 14, lon: 121 },
                        { id: 'gdacs-DR-1', kind: 'drought', title: 'Drought', detail: '', severity: 'minor', url: 'https://www.gdacs.org', lat: 45, lon: 15 }
                    ]);
                    window.tacticalGlobe.setInternetOutages([
                        { id: 'ioda-NG', kind: 'internet', title: 'Internet outage - Nigeria', detail: 'Seen by IODA in routing (BGP)', severity: 'moderate', url: 'https://ioda.inetintel.cc.gatech.edu/country/NG', lat: 9.6, lon: 8.1 }
                    ]);
                    window.tacticalGlobe.clearInternetOutages();
                    // The globe opens centred on 39.8, -98.5: this square's marker sits mid-canvas.
                    window.tacticalGlobe.setConvergence([
                        { south: 39.3, west: -99, north: 40.3, east: -98, lat: 39.8, lon: -98.5, label: 'Smith Center, United States', kinds: ['flooding', 'news', 'severe weather'],
                          score: 81, severity: 'moderate', signals: [
                            { kind: 'flooding', title: 'Solomon River', url: 'https://water.noaa.gov/gauges/X', countryWide: false },
                            { kind: 'news', title: 'Storms hit <Kansas>', url: null, countryWide: false },
                            { kind: 'severe weather', title: 'NWS Tornado Warning', url: null, countryWide: false } ] }
                    ]);
                    return null;
                } catch (e) { return String(e && e.stack || e); }
            }
            """);
        error.Should().BeNull();
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('SIGNALS MEET')", new PageWaitForSelectorOptions { Timeout = 5000 });

        var panel = await page.InnerTextAsync(".tg-incident-panel");
        panel.Should().Contain("SIGNALS MEET - Smith Center, United States").And.Contain("FLOODING, NEWS, SEVERE WEATHER").And.Contain("81 / 100")
            .And.Contain("Storms hit <Kansas>", "titles are escaped, not parsed as markup").And.Contain("not proof that they are linked");
        (await page.GetAttributeAsync(".tg-convergence-items a", "href")).Should().Be("https://water.noaa.gov/gauges/X");

        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");
        await page.EvaluateAsync("() => { window.tacticalGlobe.clearConvergence(); window.tacticalGlobe.clearHazards(); }");
        consoleErrors.Should().BeEmpty();
    }

    // Mac app (12-11-26): a native pin click opens what the same click opens on the Cesium globe.
    // Ships aren't clickable there, so not here either; aircraft open their window (it needed a
    // Cesium viewer before), and select mode adds cameras to the watch wall.
    [Fact]
    public async Task MacApp_ShouldOpenTheMatchingWindowForANativePinClick()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser, macApp: true);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        await page.EvaluateAsync("""
            () => window.gwsNativeOverwatch.selected({ operation: 'setShips', payload: { id: 'ship-1', name: 'EVER GIVEN', lat: 30, lon: 32 } })
            """);
        await page.WaitForTimeoutAsync(200);
        (await page.QuerySelectorAsync(".tg-incident-panel")).Should().BeNull();

        await page.EvaluateAsync("""
            () => {
                window.__mockAircraftDetail = { use: 'PASSENGER', route: {}, track: [ { longitude: -98, latitude: 39 }, { longitude: -97, latitude: 40 } ] };
                window.__nativeMessages = [];
                window.gwsNativeOverwatch.selected({ operation: 'setAircraft', payload: { id: 'a1b2c3', callsign: 'UAL123', lat: 40.5, lon: -96.5, altitudeFeet: 35000 } });
            }
            """);
        await page.WaitForSelectorAsync(".tg-aircraft-panel:has-text('UAL123')", new PageWaitForSelectorOptions { Timeout = 5000 });
        await page.WaitForFunctionAsync("() => window.__nativeMessages.some(m => m.operation === 'setAircraftPath')");
        await page.ClickAsync(".tg-aircraft-panel .tg-stream-panel-close");
        (await page.EvaluateAsync<bool>("() => window.__nativeMessages.some(m => m.operation === 'clearAircraftPath')")).Should().BeTrue();

        await page.EvaluateAsync("""
            () => {
                window.tacticalGlobe.setSelectModeEnabled(true);
                window.gwsNativeOverwatch.selected({ operation: 'setCameraPins', payload: { id: 'cam-1', name: 'I-75 at Exit 1', lat: 33.7, lon: -84.4 } });
            }
            """);
        await page.WaitForSelectorAsync(".tg-selection-indicator:has-text('SELECTED: 1')", new PageWaitForSelectorOptions { Timeout = 5000 });
        (await page.QuerySelectorAsync(".tg-stream-panel:not(.tg-incident-panel)")).Should().BeNull("select mode adds the camera instead of opening it");

        // A news pin as it comes back from the native map: nulls kept, numbers as numbers.
        await page.EvaluateAsync("""
            () => window.gwsNativeOverwatch.selected({ operation: 'setWorldNews', payload: {
                id: 's-1', title: 'Floods hit Valencia', latest: 'Floods hit Valencia', country: 'Spain', place: 'Valencia, Spain',
                precision: 'city', kind: 'disaster', kindLabel: 'Disasters', status: null, statusMeaning: null, active: true, surge: null,
                lat: 39.47, lon: -0.38, eventCount: 3, outletCount: 12, reportCount: 20, lastSeen: '12-11-26 09:00', summary: null,
                href: '/admin/world-news?situation=s-1', sources: [ { outlet: 'El País', title: 'Inundaciones', url: 'https://elpais.com/x' } ] } })
            """);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('Floods hit Valencia')", new PageWaitForSelectorOptions { Timeout = 5000 });
        (await page.InnerTextAsync(".tg-incident-panel")).Should().Contain("REPORTED NEWS").And.Contain("El País: Inundaciones");
        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");

        // News stories placed at the same city share a coordinate, so the native map can't split
        // them by zooming: it sends them together and the page lists them to choose from.
        await page.EvaluateAsync("""
            () => window.gwsNativeOverwatch.selectedMany({ total: 3, items: [
                { operation: 'setWorldNews', payload: { id: 'n1', title: 'Story one', latest: 'Story one', place: 'Madrid, Spain', sources: [] } },
                { operation: 'setWorldNews', payload: { id: 'n2', title: 'Story <two>', latest: 'Story <two>', place: 'Madrid, Spain', sources: [] } },
                { operation: 'setWorldNews', payload: { id: 'n3', title: 'Story three', latest: 'Story three', place: 'Madrid, Spain', sources: [] } } ] })
            """);
        await page.WaitForSelectorAsync(".tg-native-choices:has-text('3 HERE')", new PageWaitForSelectorOptions { Timeout = 5000 });
        (await page.Locator(".tg-native-choice").CountAsync()).Should().Be(3);
        (await page.InnerTextAsync(".tg-native-choices")).Should().Contain("Story <two>", "titles are escaped, not parsed");
        await page.ClickAsync(".tg-native-choice >> nth=1");
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('REPORTED NEWS'):has-text('Story <two>')", new PageWaitForSelectorOptions { Timeout = 5000 });
        (await page.QuerySelectorAsync(".tg-native-choices")).Should().BeNull("choosing closes the list");
        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");

        await page.EvaluateAsync("""
            () => window.gwsNativeOverwatch.selected({ operation: 'setHazards', payload: { id: 'gdacs-TC-1', kind: 'cyclone', title: 'Tropical Cyclone A', detail: '', severity: 'major', url: 'https://www.gdacs.org', lat: 15.8, lon: 80.2 } })
            """);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('Tropical Cyclone A')", new PageWaitForSelectorOptions { Timeout = 5000 });
        consoleErrors.Should().BeEmpty();
    }

    // Reporting blackouts (12-11-26) ride setConvergence flagged blackout: a dashed perimeter and
    // their own window, which says silence isn't proof.
    [Fact]
    public async Task Convergence_ShouldDrawAReportingBlackout_AndOpenItsWindow()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        var error = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    // The globe opens centred on 39.8, -98.5: this marker sits mid-canvas.
                    window.tacticalGlobe.setConvergence([
                        { blackout: true, south: 39.3, west: -99, north: 40.3, east: -98, lat: 39.8, lon: -98.5,
                          label: 'M7.1 <remote> basin', kinds: ['earthquake'], severity: 'blackout',
                          reason: 'Major earthquake hazard (M7.1 remote basin) with no news coverage',
                          signals: [ { kind: 'earthquake', title: 'M7.1 remote basin', url: 'https://www.gdacs.org/eq', countryWide: false } ] }
                    ]);
                    return null;
                } catch (e) { return String(e && e.stack || e); }
            }
            """);
        error.Should().BeNull();
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('REPORTING BLACKOUT')", new PageWaitForSelectorOptions { Timeout = 5000 });

        var panel = await page.InnerTextAsync(".tg-incident-panel");
        panel.Should().Contain("REPORTING BLACKOUT - M7.1 <remote> basin", "titles are escaped, not parsed as markup")
            .And.Contain("EARTHQUAKE").And.Contain("with no news coverage").And.Contain("not proof of anything");
        (await page.GetAttributeAsync(".tg-convergence-items a", "href")).Should().Be("https://www.gdacs.org/eq");

        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");
        await page.EvaluateAsync("() => window.tacticalGlobe.clearConvergence()");
        consoleErrors.Should().BeEmpty();
    }

    // Unit 6 (06-11-26): a photo's own EXIF places it on the globe, read in the browser.
    [Fact]
    public async Task PhotoPlacement_ShouldReadWhereAndWhenAPhotoWasTaken_AndSayWhenItHasNoLocation()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        var result = await page.EvaluateAsync<System.Text.Json.JsonElement>("""
            (photos) => {
                const bytes = b64 => Uint8Array.from(atob(b64), c => c.charCodeAt(0));
                const info = window.tacticalGlobe.readPhotoExif(bytes(photos.withGps));
                const placed = window.tacticalGlobe.placePhoto('tg-viewport', info, 'IMG_0001.jpg');
                const none = window.tacticalGlobe.placePhoto('tg-viewport', window.tacticalGlobe.readPhotoExif(bytes(photos.withoutGps)), 'IMG_0002.jpg');
                const notAPhoto = window.tacticalGlobe.readPhotoExif(new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
                return { info, placed, none, notAPhoto };
            }
            """, new { withGps = Convert.ToBase64String(ExifJpeg(withGps: true)), withoutGps = Convert.ToBase64String(ExifJpeg(withGps: false)) });

        var info = result.GetProperty("info");
        info.GetProperty("latitude").GetDouble().Should().BeApproximately(32.6485, 0.0001);
        info.GetProperty("longitude").GetDouble().Should().BeApproximately(-83.6675, 0.0001, "W is negative");
        info.GetProperty("heading").GetDouble().Should().Be(123.5);
        info.GetProperty("takenAt").GetString().Should().Be("05-11-26 14:03", "dd-MM-yy HH:mm");
        info.GetProperty("make").GetString().Should().Be("Apple");
        result.GetProperty("placed").GetString().Should().Be("PHOTO PLACED · TAKEN 05-11-26 14:03 · FACING 124°");
        result.GetProperty("none").GetString().Should().StartWith("NO LOCATION IN THIS PHOTO");
        result.GetProperty("notAPhoto").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        consoleErrors.Should().BeEmpty();
    }

    // A JPEG holding only an EXIF block (big-endian TIFF): Make, DateTimeOriginal and, optionally,
    // GPS 32°38'54.6"N 83°40'03"W facing 123.5°.
    internal static byte[] ExifJpeg(bool withGps)
    {
        var tiff = new byte[216];
        void U16(int at, int value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), (ushort)value);
        void U32(int at, long value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), (uint)value);
        void Entry(int at, int tag, int type, int count, long value) { U16(at, tag); U16(at + 2, type); U32(at + 4, count); U32(at + 8, value); }
        void Text(int at, string text) => System.Text.Encoding.ASCII.GetBytes(text + "\0").CopyTo(tiff, at);
        void Rational(int at, long numerator, long denominator) { U32(at, numerator); U32(at + 4, denominator); }

        tiff[0] = (byte)'M'; tiff[1] = (byte)'M'; U16(2, 42); U32(4, 8);
        // IFD0 at 8: Make, the Exif IFD, the GPS IFD.
        U16(8, withGps ? 3 : 2);
        Entry(10, 0x010F, 2, 6, 50);
        Entry(22, 0x8769, 4, 1, 56);
        if (withGps) Entry(34, 0x8825, 4, 1, 94);
        Text(50, "Apple");
        // Exif IFD at 56: DateTimeOriginal.
        U16(56, 1);
        Entry(58, 0x9003, 2, 20, 74);
        Text(74, "2026:11:05 14:03:22");
        // GPS IFD at 94.
        U16(94, 5);
        Entry(96, 1, 2, 2, 0x4E000000);
        Entry(108, 2, 5, 3, 160);
        Entry(120, 3, 2, 2, 0x57000000);
        Entry(132, 4, 5, 3, 184);
        Entry(144, 17, 5, 1, 208);
        Rational(160, 32, 1); Rational(168, 38, 1); Rational(176, 546, 10);
        Rational(184, 83, 1); Rational(192, 40, 1); Rational(200, 3, 1);
        Rational(208, 1235, 10);

        var app1Length = 2 + 6 + tiff.Length;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(app1Length >> 8), (byte)app1Length, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. tiff, 0xFF, 0xD9];
    }

    // 07-11-26 (Grant: "If it has data, I want to be able to view that data"): a satellite's window
    // shows what it sees now and lays its NASA GIBS imagery over the globe (the CSP lets the tiles
    // in), and the ISS's shows who's aboard.
    [Fact]
    public async Task Satellites_ShouldShowTheirPublicData_AndLayTheirImageryOverTheGlobe()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        var tiles = 0;
        await page.RouteAsync("https://gibs.earthdata.nasa.gov/**", route =>
        {
            Interlocked.Increment(ref tiles);
            return route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "image/png",
                Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
                BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==")
            });
        });
        (await InitAsync(page)).Should().BeNull();

        var error = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    window.__imagery = [];
                    const add = Cesium.ImageryLayerCollection.prototype.addImageryProvider;
                    Cesium.ImageryLayerCollection.prototype.addImageryProvider = function (provider, index) {
                        const layer = add.call(this, provider, index);
                        window.__imagery.push({ url: String(provider.url || ''), layer: layer, removed: false });
                        return layer;
                    };
                    const remove = Cesium.ImageryLayerCollection.prototype.remove;
                    Cesium.ImageryLayerCollection.prototype.remove = function (layer, destroy) {
                        window.__imagery.forEach(function (i) { if (i.layer === layer) i.removed = true; });
                        return remove.call(this, layer, destroy);
                    };
                    window.tacticalGlobe.setSatellites([
                        { id: '43013', name: 'NOAA-20', group: 'whole-Earth imagery', lat: 39.8, lon: -98.5, altitudeKm: 825, periodMinutes: 101.4, with: [], path: [],
                          data: { operator: 'NOAA', about: 'VIIRS true-colour imagery of the whole Earth every day.',
                                  layer: 'VIIRS_NOAA20_CorrectedReflectance_TrueColor', tileMatrixSet: 'GoogleMapsCompatible_Level9', format: 'image/jpeg',
                                  time: '2026-11-06', maxLevel: 9,
                                  snapshot: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
                                  snapshotCaption: 'Below it now, as NOAA-20 saw it on 06-11-26 (NASA GIBS)', crew: [], link: 'https://www.nesdis.noaa.gov/' } }
                    ]);
                    return null;
                } catch (e) { return String(e && e.stack || e); }
            }
            """);
        error.Should().BeNull();
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('NOAA-20')", new PageWaitForSelectorOptions { Timeout = 5000 });
        (await page.InnerTextAsync(".tg-incident-panel")).Should().Contain("WHOLE-EARTH IMAGERY").And.Contain("RUN BY").And.Contain("IMAGERY FROM")
            .And.Contain("06-11-26").And.Contain("Below it now, as NOAA-20 saw it");
        (await page.Locator(".tg-sat-snapshot img").CountAsync()).Should().Be(1);

        await page.ClickAsync(".tg-sat-imagery");
        (await page.EvaluateAsync<string>("() => window.__imagery.length ? window.__imagery[0].url : ''")).Should()
            .Contain("gibs.earthdata.nasa.gov/wmts/epsg3857/best/VIIRS_NOAA20_CorrectedReflectance_TrueColor/default/2026-11-06/GoogleMapsCompatible_Level9/");
        (await page.TextContentAsync(".tg-sat-imagery")).Should().Be("HIDE ITS IMAGERY");
        await page.WaitForTimeoutAsync(1500);
        tiles.Should().BeGreaterThan(0, "the globe asks GIBS for the imagery's tiles, which the CSP allows");

        await page.ClickAsync(".tg-sat-imagery");
        (await page.EvaluateAsync<bool>("() => window.__imagery[0].removed")).Should().BeTrue();
        (await page.TextContentAsync(".tg-sat-imagery")).Should().Be("SHOW ITS IMAGERY ON THE GLOBE");

        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");
        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setSatellites([
                { id: '25544', name: 'ISS', group: 'space station', lat: 39.8, lon: -98.5, altitudeKm: 415, periodMinutes: 92.9, with: [], path: [],
                  data: { operator: 'NASA, Roscosmos, ESA, JAXA and CSA', about: 'The International Space Station, crewed, about 400 km up.',
                          layer: null, tileMatrixSet: null, format: null, time: null, maxLevel: 0, snapshot: null, snapshotCaption: null,
                          crew: ['Anil Menon', 'Anna Kikina'], link: 'https://www.nasa.gov/international-space-station/' } }
            ])
            """);
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('ABOARD NOW (2)')", new PageWaitForSelectorOptions { Timeout = 5000 });
        (await page.InnerTextAsync(".tg-incident-panel")).Should().Contain("Anil Menon, Anna Kikina");
        (await page.Locator(".tg-sat-imagery").CountAsync()).Should().Be(0, "the ISS has no imagery to lay over the globe");
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
    // and a SURGING marker's pulse keeps it drawing only while one is on the globe. Measured as the
    // share of animation ticks that drew, not frames per second: CI's software WebGL draws only a few
    // frames a second (it managed 4 in 1.5 s), so a frame count says more about the machine.
    [Fact]
    public async Task IdleGlobe_ShouldDrawOnlyOnChanges_AndKeepDrawingWhileANewsMarkerPulses()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await page.EvaluateAsync("""
            () => {
              // Cesium's exports are read-only, but its prototypes aren't; render() runs on every
              // animation tick and postRender only when a frame is actually drawn.
              window.__ticks = 0;
              window.__drawn = 0;
              const render = Cesium.Scene.prototype.render;
              Cesium.Scene.prototype.render = function (time) {
                if (!this.__counted) {
                  this.__counted = true;
                  window.__scene = this;
                  this.postRender.addEventListener(function () { window.__drawn++; });
                }
                window.__ticks++;
                return render.call(this, time);
              };
            }
            """);
        await InitAsync(page);
        // Tile loads draw frames too, so measure once they've landed (or after 15 s if one never does).
        try
        {
            await page.WaitForFunctionAsync("() => window.__scene && window.__scene.globe.tilesLoaded", null,
                new PageWaitForFunctionOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
        }
        await page.WaitForTimeoutAsync(500);

        // Measured in the browser's own time, over at least 40 ticks and 3 s: a loaded CI runner
        // managed 4 ticks in 3 s of the test's time (13-11-26), and a share of ticks drawn means
        // nothing when the tick rate itself collapses. So the checks are rates: idle draws about
        // once a second; a pulse draws at least 4 a second (it asks for ~20; a loaded machine gave
        // 9), or half the ticks when the browser can't even tick that fast.
        async Task<(int Ticks, int Drawn, double Seconds)> MeasureAsync()
        {
            var before = await page.EvaluateAsync<double[]>("() => [window.__ticks, window.__drawn, performance.now()]");
            await page.WaitForFunctionAsync(
                "start => window.__ticks - start[0] >= 40 && performance.now() - start[2] >= 3000", before,
                new PageWaitForFunctionOptions { Timeout = 30000, PollingInterval = 100 });
            var after = await page.EvaluateAsync<double[]>("() => [window.__ticks, window.__drawn, performance.now()]");
            return ((int)(after[0] - before[0]), (int)(after[1] - before[1]), (after[2] - before[2]) / 1000);
        }

        void ShouldBeIdle((int Ticks, int Drawn, double Seconds) measured, string because) =>
            measured.Drawn.Should().BeLessThanOrEqualTo((int)Math.Ceiling(measured.Seconds) + 2, because);

        ShouldBeIdle(await MeasureAsync(), "nothing changed, so only the once-a-second frame draws (it was every tick)");

        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setWorldNews([{
              id: 'situation-1', title: 'United States rail disruption', latest: 'Services begin to recover',
              country: 'United States', lat: 39.8, lon: -98.5, eventCount: 3, outletCount: 12,
              reportCount: 18, lastSeen: '29-10-26 10:30', surge: 'Coverage doubled in 2 hours',
              href: '/admin/world-news?situation=situation-1', sources: []
            }])
            """);
        var pulsing = await MeasureAsync();
        pulsing.Drawn.Should().BeGreaterThanOrEqualTo((int)Math.Min(pulsing.Ticks * 0.5, pulsing.Seconds * 4),
            "the SURGING pulse asks for about 20 frames a second while it's there");

        await page.EvaluateAsync("() => window.tacticalGlobe.clearWorldNews()");
        await page.WaitForTimeoutAsync(300);
        ShouldBeIdle(await MeasureAsync(), "the pulse stopped with the marker");
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
                                    precise: true, cameras: [], alerts: ['Flood Warning'], hazards: [], outages: [],
                                    aircraft: [{ callsign: 'NH123', country: 'Japan', miles: 4.2, altitudeFeet: 29000, speedKnots: 420 }],
                                    ships: [{ name: 'Kanto Maru', type: 'Cargo', destination: 'Tokyo', miles: 8.1, speedKnots: 12, seenAt: '10-11-26 14:05' }],
                                    aircraftNote: null, shipsNote: null },
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
        context.Should().Contain("USGS: M 5.4 - 12 km NE of Tokyo, Japan").And.Contain("WEATHER ALERTS").And.Contain("Flood Warning")
            .And.Contain("AIRCRAFT NEARBY").And.Contain("Flight NH123").And.Contain("VESSELS NEARBY").And.Contain("Kanto Maru")
            .And.Contain("do not confirm the reported event");

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
    internal const string RadarTimelineScript = """
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
              { validUtc: iso(0), kind: 'Observed', tileUrlTemplate: base + 'nexrad-n0q-900913/{z}/{x}/{y}.png',
                globalTileUrlTemplate: 'https://tilecache.rainviewer.com/v2/radar/test/256/{z}/{x}/{y}/2/1_1.png' },
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
    internal static async Task ZoomInBelowHoverThresholdAsync(IPage page)
    {
        await page.EvaluateAsync("() => window.tacticalGlobe.flyTo('tg-viewport', 38.8977, -77.0365, 1500, 0)");
        await page.Mouse.MoveAsync(400, 300);
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
