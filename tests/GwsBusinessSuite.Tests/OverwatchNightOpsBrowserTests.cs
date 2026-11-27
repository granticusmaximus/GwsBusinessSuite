using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;
using Microsoft.Playwright;
using static GwsBusinessSuite.Tests.OverwatchGridScriptBrowserTests;

namespace GwsBusinessSuite.Tests;

// Night Ops x Apple, combined (20-11-26) on the real script and pinned Cesium build: the Mac's disc per
// layer (Mac look, 26-11-26), target brackets, thermal tint, line of sight and the quality setting.
[Collection("PlaywrightOverwatchB")]
public sealed class OverwatchNightOpsBrowserTests(PlaywrightBrowserFixture fixture)
{
    // The Mac look (26-11-26, Grant: the Mac app is the reference). Every layer sample, sent to the real
    // script, draws the very disc the Mac draws for it: the style comes from the Mac's own code
    // (NativeShapes.Payload/Items/Marker on the same arguments) and the image from the web's macDisc.
    // Layers drawn as lines, fills or tool marks (alerts, news shading, city traffic, routes, measure)
    // and pins that are a callout box are left out; camera alert highlights restyle existing pins.
    private static readonly HashSet<string> NotDiscs = new(StringComparer.Ordinal)
    {
        "setWeatherAlerts", "setNewsShading", "setRoadTraffic", "setRoute", "setRouteHighlights", "setRouteConditions",
        "setMeasure", "setCameraAlertHighlights",
    };

    // The harness page has no server behind it: layers that fetch map tiles (radar, traffic, imagery,
    // terrain) log "Failed to load resource". These tests check drawing, so only script errors count.
    private static IEnumerable<string> ScriptErrors(IEnumerable<string> consoleErrors) =>
        consoleErrors.Where(error => !error.StartsWith("Failed to load resource", StringComparison.Ordinal));

    [Fact]
    public async Task EveryMarkerLayer_ShouldDrawTheMacsDisc_AndAlertsShouldBeHatched()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();
        await page.EvaluateAsync("""
            () => {
              window.__images = []; window.__lines = 0;
              const add = Cesium.EntityCollection.prototype.add;
              Cesium.EntityCollection.prototype.add = function (e) {
                if (e && e.billboard && typeof e.billboard.image === 'string') window.__images.push(e.billboard.image);
                if (e && e.polyline) window.__lines++;
                return add.apply(this, arguments);
              };
              const addBillboard = Cesium.BillboardCollection.prototype.add;
              Cesium.BillboardCollection.prototype.add = function (o) {
                if (o && typeof o.image === 'string') window.__images.push(o.image);
                return addBillboard.apply(this, arguments);
              };
            }
            """);

        var wrong = new List<string>();
        var checkedLayers = 0;
        foreach (var sample in OverwatchLayerSample.All)
        {
            await page.EvaluateAsync("() => { window.__images = []; window.__lines = 0; }");
            await page.EvaluateAsync($"json => Promise.resolve(window.tacticalGlobe.{sample.Op}(...JSON.parse(json)))", sample.Args.GetRawText());
            if (sample.Layer == "Weather alerts")
            {
                (await page.EvaluateAsync<int>("() => window.__lines")).Should().BeGreaterThan(3, "a weather alert is a hatched area");
                continue;
            }
            if (NotDiscs.Contains(sample.Op)) continue;
            var payload = NativeShapes.Payload(sample.Op, sample.MacArguments());
            var styles = NativeShapes.Items(payload)
                .Where(item => NativeShapes.TryPoint(item, out _, out _) && !(item.TryGetValue("callout", out var c) && c is string { Length: > 0 }))
                .Select(item => NativeShapes.Marker(sample.Op, item)).Distinct().ToList();
            if (styles.Count == 0) { wrong.Add($"{sample.Layer}: the Mac draws no pin for the sample"); continue; }
            checkedLayers++;
            foreach (var style in styles)
            {
                var drawn = await page.EvaluateAsync<bool>("([symbol, tone, prominent]) => window.__images.includes(window.tacticalGlobe.macDisc(symbol, tone, prominent, false))",
                    new object[] { style.Symbol, style.Tone.ToString(), style.Prominent });
                if (!drawn) wrong.Add($"{sample.Layer} (expected the Mac's {style.Symbol} {style.Tone}{(style.Prominent ? " prominent" : "")} disc)");
            }
        }

        wrong.Should().BeEmpty("every layer draws the Mac's disc");
        checkedLayers.Should().BeGreaterThan(18, "the check covers the pin layers");
        (await page.EvaluateAsync<string[]>("() => ['Red', 'Orange', 'Yellow', 'Gray', 'Green', 'Teal', 'Cyan', 'Blue', 'Purple', 'Pink'].map(t => window.tacticalGlobe.macToneHex(t))"))
            .Should().Equal(new[] { "Red", "Orange", "Yellow", "Gray", "Green", "Teal", "Cyan", "Blue", "Purple", "Pink" }
                .Select(t => NativeShapes.ToneHex(Enum.Parse<NativeShapes.MarkerTone>(t))), "the web's tones are the Mac's");
        ScriptErrors(consoleErrors).Should().BeEmpty();
    }

    [Fact]
    public async Task SelectingAPin_ShouldShowTargetBrackets_UntilItsWindowCloses()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        // At the default camera's look-at point, so it lands at the centre of the canvas.
        await page.EvaluateAsync("""
            () => window.tacticalGlobe.setHazards([{ id: 'h1', kind: 'earthquake', title: 'M5.8 earthquake', detail: '', severity: 'major',
              url: 'https://example.test', lat: 39.8, lon: -98.5 }])
            """);
        await page.WaitForTimeoutAsync(1000);
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().brackets")).Should().BeFalse();

        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-incident-panel')", null, new PageWaitForFunctionOptions { Timeout = 5000 });
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().brackets")).Should().BeTrue("the selected pin is bracketed");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.nightOpsState().bracketLabel")).Should().StartWith("RNG ");

        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().brackets")).Should().BeFalse("closing its window deselects it");
    }

    [Fact]
    public async Task ThermalTint_ShouldWarmFiresAndEngines_BelowOrbit_UnlessQualityIsOff()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setQuality('tg-viewport', 'high');
              window.tacticalGlobe.setFireHotspots([{ id: 'f1', kind: 'fire', title: 'Fire hotspots (12)', detail: 'VIIRS', severity: 'moderate', url: '', lat: 39.8, lon: -98.5 }]);
              window.tacticalGlobe.setAircraft('tg-viewport', [{ id: 'p1', callsign: 'DAL1', lat: 39.9, lon: -98.4, altitudeFeet: 30000, speedKnots: 450, heading: 90 }]);
            }
            """);
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().thermal")).Should().BeFalse("no thermal tint from orbit");

        await page.EvaluateAsync("() => window.tacticalGlobe.flyTo('tg-viewport', 39.8, -98.5, 400000, 0)");
        await page.WaitForFunctionAsync("window.tacticalGlobe.nightOpsState().stage === 'regional'", null, new PageWaitForFunctionOptions { Timeout = 5000 });
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().thermal")).Should().BeTrue("fires and engines read warm from the regional stage");

        await page.EvaluateAsync("() => window.tacticalGlobe.setQuality('tg-viewport', 'off')");
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().thermal")).Should().BeFalse("quality Off drops it");
        ScriptErrors(consoleErrors).Should().BeEmpty();
    }

    // Looks (21-11-26; the Mac look 26-11-26): every look is real Earth imagery, as on the Mac's Apple
    // map. Standard and Night Ops keep the green coastlines over it with the road overlay faint;
    // Satellite drops the coastlines and shows roads at full strength (Apple's hybrid). An unknown look
    // changes nothing.
    [Fact]
    public async Task Looks_ShouldShowRealImagery_WithCoastlinesOrRoadsByLook()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await InitAsync(page);
        const string Look = "() => JSON.stringify(window.tacticalGlobe.nightOpsState().look)";
        (await page.EvaluateAsync<string>(Look)).Should().Be("{\"name\":\"night-ops\",\"imagery\":false,\"land\":true,\"roads\":false}");

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setLook('tg-viewport', 'satellite')")).Should().Be("satellite");
        (await page.EvaluateAsync<string>(Look)).Should().Be("{\"name\":\"satellite\",\"imagery\":true,\"land\":false,\"roads\":true}");

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setLook('tg-viewport', 'nonsense')")).Should().Be("satellite");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setLook('tg-viewport', 'standard')")).Should().Be("standard");
        (await page.EvaluateAsync<string>(Look)).Should().Be("{\"name\":\"standard\",\"imagery\":true,\"land\":true,\"roads\":false}");
        ScriptErrors(consoleErrors).Should().BeEmpty();
    }

    [Fact]
    public async Task Quality_ShouldBeRemembered_DefaultToStandardOnBattery_AndGateShadingAndBuildings()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await page.EvaluateAsync("() => Object.defineProperty(navigator, 'getBattery', { configurable: true, value: () => Promise.resolve({ charging: false }) })");
        await page.RouteAsync("**/admin/api/overwatch/terrain/**", route => route.FulfillAsync(new() { ContentType = "image/png",
            BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNoYGAAAAGEAIH5/mWIAAAAAElFTkSuQmCC") }));
        await InitAsync(page);

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.getQuality()")).Should().Be("standard", "High is never the default on battery");
        (await page.EvaluateAsync<string>("() => document.querySelector('.tg-shell').dataset.owQuality")).Should().Be("standard");

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', true)")).Should().Be("on");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setQuality('tg-viewport', 'off')")).Should().Be("off");
        (await page.EvaluateAsync<string?>("() => window.localStorage.getItem('gws-overwatch-quality')")).Should().Be("off");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setTerrainShading('tg-viewport', 'contours')")).Should().Be("failed: quality is Off");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setQuality('tg-viewport', 'standard')")).Should().Be("standard");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setTerrainShading('tg-viewport', 'contours')")).Should().Be("contours");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setQuality('tg-viewport', 'nonsense')")).Should().Be("standard");

        // A stored choice wins over the battery.
        await page.EvaluateAsync("() => window.localStorage.setItem('gws-overwatch-quality', 'high')");
        await page.ReloadAsync();
        await page.EvaluateAsync("() => Object.defineProperty(navigator, 'getBattery', { configurable: true, value: () => Promise.resolve({ charging: false }) })");
        await InitAsync(page);
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.getQuality()")).Should().Be("high");
    }

    [Fact]
    public async Task LineOfSight_ShouldDarkenWhatTheCameraCantSee_AndClear()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        await page.RouteAsync("**/admin/api/overwatch/terrain/**", route => route.FulfillAsync(new() { ContentType = "image/png",
            BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNoYGAAAAGEAIH5/mWIAAAAAElFTkSuQmCC") }));
        await InitAsync(page);

        // The visibility walk: a 30 m ridge two cells east of a camera 8 m up hides the ground behind it.
        var mask = await page.EvaluateAsync<int[]>("""
            () => {
              const n = 10, h = new Float64Array(n * n);
              for (let i = 0; i < n; i++) h[i * n + 6] = 30;
              return Array.from(window.tacticalGlobe.lineOfSightMask(h, n, 8, 2));
            }
            """);
        mask[4 * 10 + 8].Should().Be(1, "behind the ridge is hidden");
        mask[4 * 10 + 2].Should().Be(0, "open ground west of the camera is seen");
        mask[0].Should().Be(2, "the grid's corner is outside the 3 km circle");

        await page.EvaluateAsync("() => window.tacticalGlobe.setQuality('tg-viewport', 'standard')");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.runLineOfSight('tg-viewport', 39.8, -98.5)")).Should().StartWith("failed:");
        await page.EvaluateAsync("() => window.tacticalGlobe.setQuality('tg-viewport', 'high')");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.runLineOfSight('tg-viewport', 39.8, -98.5)")).Should().StartWith("failed:", "3D terrain is off");

        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', true)")).Should().Be("on");
        await page.EvaluateAsync("""
            () => window.tacticalGlobe.openCamera({ id: 'c1', name: 'Ridge cam', lat: 39.8, lon: -98.5, streamUrl: 'https://example.test/c.jpg',
              streamKind: 'Snapshot', sourceName: 'Test', sourceAttributionUrl: '' })
            """);
        await page.ClickAsync(".tg-los-button");
        await page.WaitForFunctionAsync("/WITHIN 3 KM/.test(document.querySelector('.tg-los-status').textContent)", null, new PageWaitForFunctionOptions { Timeout = 15000 });
        (await page.EvaluateAsync<int>("() => window.tacticalGlobe.nightOpsState().lineOfSight.shapes")).Should().BeGreaterThan(0, "the 3 km ring is drawn");
        (await page.EvaluateAsync<bool>("() => !!document.querySelector('.tg-los-clear')")).Should().BeTrue();

        await page.ClickAsync(".tg-stream-panel-close");
        await page.EvaluateAsync("() => document.querySelector('.tg-los-clear').click()");
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.nightOpsState().lineOfSight === null && !document.querySelector('.tg-los-clear')")).Should().BeTrue();
        ScriptErrors(consoleErrors).Should().BeEmpty();
    }
}
