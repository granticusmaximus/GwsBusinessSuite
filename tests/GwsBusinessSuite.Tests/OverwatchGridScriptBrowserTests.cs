using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Regression guard for two real incidents, both only reproducible in a real browser against
// the real shipped script - there is no server-side signal for either class of bug:
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
        "connect-src 'self' wss: ws: https://nominatim.openstreetmap.org https://*.azurewebsites.net https://cdn.jsdelivr.net https://server.arcgisonline.com https://tile.openstreetmap.org https://*.tile.openstreetmap.org https://nowcoast.noaa.gov; " +
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

    // Real production styling for the drag/resize-relevant classes lives in
    // OverwatchGrid.razor.css (a Blazor-scoped stylesheet), which this bare-JS harness never
    // loads - without it, .tg-stream-panel etc. have no `position: absolute` at all, so
    // style.left/top set by makeDraggableAndResizable would be entirely inert (a real gap this
    // duplicates just enough of to make drag/resize testable, confirmed empirically: the drag/
    // resize tests read a stale, unpositioned 0-diff before this was added).
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
            .tg-stream-panel { position: absolute; top: 3.6rem; right: 1.1rem; width: 320px; min-width: 240px; min-height: 160px; }
            .tg-weather-panel { position: absolute; bottom: 2.6rem; left: 1.1rem; display: none; }
            .tg-panel-resize-handle { position: absolute; right: 2px; bottom: 2px; width: 14px; height: 14px; }
            .tg-watch-wall-panel { position: absolute; top: 3.6rem; right: 1.1rem; width: 560px; height: 420px; min-width: 320px; min-height: 240px; }
            .tg-watch-wall-grid { display: grid; }
            .tg-selection-indicator { position: absolute; bottom: 2.6rem; right: 1.1rem; }
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
            ContentType = "application/javascript",
            Body = script
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
                if (methodName === 'BuildShareLinkAsync') {
                  return Promise.resolve(window.__mockShareLink || 'https://example.test/admin/osint?v=stub');
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

    [Fact]
    public async Task StreamPanel_ShouldBeDraggable_ViaItsHeader()
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

        var beforeTop = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().top");

        var headerBox = await page.EvaluateAsync<double[]>("""
            () => {
              const r = document.querySelector('.tg-stream-panel-header').getBoundingClientRect();
              return [r.left + r.width / 2, r.top + r.height / 2];
            }
            """);
        await page.Mouse.MoveAsync((float)headerBox[0], (float)headerBox[1]);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(headerBox[0] + 40), (float)(headerBox[1] + 120), new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();

        var afterTop = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().top");
        (afterTop - beforeTop).Should().BeApproximately(120, 5, "dragging the header by 120px vertically should move the panel by roughly the same amount");
    }

    [Fact]
    public async Task StreamPanel_ShouldBeResizable_ViaTheHandle()
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
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-panel-resize-handle')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var beforeWidth = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().width");

        var handleBox = await page.EvaluateAsync<double[]>("""
            () => {
              const r = document.querySelector('.tg-panel-resize-handle').getBoundingClientRect();
              return [r.left + r.width / 2, r.top + r.height / 2];
            }
            """);
        await page.Mouse.MoveAsync((float)handleBox[0], (float)handleBox[1]);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(handleBox[0] + 100), (float)(handleBox[1] + 60), new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();

        var afterWidth = await page.EvaluateAsync<double>("document.querySelector('.tg-stream-panel').getBoundingClientRect().width");
        (afterWidth - beforeWidth).Should().BeApproximately(100, 5, "dragging the resize handle 100px right should widen the panel by roughly the same amount");
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
        await page.WaitForFunctionAsync(
            "document.querySelector('.tg-stream-panel-analyze').textContent === 'ANALYZING...'",
            new PageWaitForFunctionOptions { Timeout = 5000 });

        var duringDisabled = await page.EvaluateAsync<bool>("document.querySelector('.tg-stream-panel-analyze').disabled");
        duringDisabled.Should().BeTrue();

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
    private static async Task ZoomInBelowHoverThresholdAsync(IPage page)
    {
        await page.Mouse.MoveAsync(400, 300);
        for (var i = 0; i < 50; i++)
        {
            await page.Mouse.WheelAsync(0, -300);
            await page.WaitForTimeoutAsync(100);
        }
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
