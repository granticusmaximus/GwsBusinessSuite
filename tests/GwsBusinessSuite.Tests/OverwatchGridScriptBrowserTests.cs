using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Regression guard for two real incidents, both only reproducible in a real browser against
// the real shipped script - there is no server-side signal for either class of bug:
//
// 1. OpenStreetMapImageryProvider.fromUrl doesn't exist on the pinned Cesium version (it's
//    still a plain synchronous constructor, unlike several other Cesium imagery providers), so
//    init() threw synchronously the moment anyone opened Overwatch Grid. Left uncaught, that
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
        "img-src 'self' data: https:; " +
        "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; " +
        "connect-src 'self' wss: ws: https://nominatim.openstreetmap.org https://*.azurewebsites.net https://cdn.jsdelivr.net https://server.arcgisonline.com https://tile.openstreetmap.org https://*.tile.openstreetmap.org https://nowcoast.noaa.gov; " +
        "media-src 'self' blob: https:; " +
        "worker-src 'self' blob:; " +
        "frame-src 'self'; " +
        "frame-ancestors 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';";

    private static string ScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

    private const string HarnessHtml = """
        <!doctype html>
        <html>
        <head>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Widgets/widgets.css" />
          <script src="https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Cesium.js"></script>
          <script src="/js/tactical-globe.js"></script>
        </head>
        <body>
          <div class="tg-shell" style="position:relative;width:800px;height:600px;">
            <div id="tg-viewport" style="position:absolute;inset:0;"></div>
          </div>
          <button data-tg-toggle="radar">radar</button>
          <button data-tg-toggle="alerts">alerts</button>
          <input type="text" id="tg-search-input" />
          <button type="button" id="tg-search-button">GO</button>
          <span id="tg-search-status"></span>
        </body>
        </html>
        """;

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
        await InitAsync(page);

        var result = await page.EvaluateAsync<string?>("""
            () => {
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
                window.tacticalGlobe.setRadarVisible(false);
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
    public async Task LocationSearch_ShouldFlyToTheGeocodedResult_OnGoClick()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
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
