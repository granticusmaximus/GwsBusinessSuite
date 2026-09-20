using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Regression guard for a real incident: OpenStreetMapImageryProvider.fromUrl doesn't exist on
// the pinned Cesium version (it's still a plain synchronous constructor, unlike several other
// Cesium imagery providers), so tactical-globe.js's init() threw synchronously the moment anyone
// opened Overwatch Grid. Left uncaught, that JSException-from-interop crashed the whole Blazor
// Server circuit - reported by the user as "nothing opens" for the *entire app*, not just this
// one page. There is no server-side signal for a client-only JS bug like this - only a real
// browser running the real shipped script (loading the real Cesium build from jsdelivr, exactly
// as production does) actually exercises the code path that broke. Mirrors
// CmsCanvasSectionSelectionBrowserTests.cs's own "drive the real script in a browser" approach.
[Collection("Playwright")]
public sealed class OverwatchGridScriptBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string ScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

    private static async Task<IPage> OpenHarnessAsync(IBrowser browser)
    {
        var script = await File.ReadAllTextAsync(ScriptPath);
        var html = $$"""
            <!doctype html>
            <html>
            <head>
              <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Widgets/widgets.css" />
              <script src="https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Cesium.js"></script>
            </head>
            <body>
              <div id="tg-viewport" style="width:800px;height:600px;"></div>
              <button data-tg-toggle="radar">radar</button>
              <button data-tg-toggle="alerts">alerts</button>
              <script>{{script}}</script>
              <script>
                var fakeDotNetRef = { invokeMethodAsync: function () { return Promise.resolve(); } };
                window.__ready = false;
                window.__error = null;
                try {
                  window.tacticalGlobe.init('tg-viewport', fakeDotNetRef);
                  window.__ready = true;
                } catch (e) {
                  window.__error = e.message;
                }
              </script>
            </body>
            </html>
            """;

        var page = await browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = html
        }));
        await page.GotoAsync("http://localhost/overwatch-grid-harness");
        await page.WaitForFunctionAsync("window.__ready === true || window.__error !== null", new PageWaitForFunctionOptions { Timeout = 10000 });
        return page;
    }

    [Fact]
    public async Task Init_ShouldSucceed_AgainstTheRealPinnedCesiumBuild()
    {
        var page = await OpenHarnessAsync(fixture.Browser);

        var error = await page.EvaluateAsync<string?>("window.__error");

        error.Should().BeNull("tacticalGlobe.init() must not throw against the real Cesium build this app loads in production");
    }

    [Fact]
    public async Task AfterInit_EveryExportedFunction_ShouldRunWithoutThrowing()
    {
        var page = await OpenHarnessAsync(fixture.Browser);

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
}
