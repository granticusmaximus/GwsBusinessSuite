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
        "connect-src 'self' wss: ws: https://nominatim.openstreetmap.org https://*.azurewebsites.net https://cdn.jsdelivr.net https://tile.openstreetmap.org https://*.tile.openstreetmap.org https://nowcoast.noaa.gov; " +
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
          <div id="tg-viewport" style="width:800px;height:600px;"></div>
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

    private static async Task<string?> InitAsync(IPage page) => await page.EvaluateAsync<string?>("""
        () => {
          try {
            window.tacticalGlobe.init('tg-viewport', { invokeMethodAsync: function () { return Promise.resolve(); } });
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
        await page.RouteAsync("https://nominatim.openstreetmap.org/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """[{"lat":"33.7490","lon":"-84.3880"}]"""
        }));

        await page.FillAsync("#tg-search-input", "Atlanta, GA");
        await page.ClickAsync("#tg-search-button");
        await page.WaitForFunctionAsync("document.getElementById('tg-search-status').textContent === ''", new PageWaitForFunctionOptions { Timeout = 5000 });

        var status = await page.EvaluateAsync<string>("document.getElementById('tg-search-status').textContent");
        status.Should().BeEmpty("a successful geocode clears the status text rather than leaving a stale message");
    }

    [Fact]
    public async Task LocationSearch_ShouldShowNotFound_WhenNominatimReturnsNoResults()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await InitAsync(page);
        await page.RouteAsync("https://nominatim.openstreetmap.org/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = "[]"
        }));

        await page.FillAsync("#tg-search-input", "a place that does not exist anywhere");
        await page.ClickAsync("#tg-search-button");
        await page.WaitForFunctionAsync("document.getElementById('tg-search-status').textContent === 'NOT FOUND'", new PageWaitForFunctionOptions { Timeout = 5000 });
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
