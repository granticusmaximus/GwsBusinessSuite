using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using static GwsBusinessSuite.Tests.OverwatchGridScriptBrowserTests;

namespace GwsBusinessSuite.Tests;

// The second half of OverwatchGridScriptBrowserTests (12-11-26): each half has its own collection and
// browser, so the two run side by side instead of one 160 s chain. Helpers live in the first half.
[Collection("PlaywrightOverwatchB")]
public sealed class OverwatchGridScriptBrowserTestsB(PlaywrightBrowserFixture fixture)
{
    // Overwatch's boot screen lifts on this (30-10-26) instead of after a fixed 1.8 s.
    [Fact]
    public async Task ShaderCompileFailure_BeforeViewerIsAvailable_ShouldFallBackWithoutThrowing()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;

        var result = await page.EvaluateAsync<string>("""
            () => {
                const handled = window.tacticalGlobe.reportRenderError(
                    'tg-viewport',
                    new Error('Fragment shader failed to compile. Compile log: test'));
                return JSON.stringify({ handled, fallback: document.getElementById('tg-viewport').dataset.globeFallback || 'missing' });
            }
            """);

        result.Should().Contain("\"fallback\":\"true\"");
        result.Should().Contain("\"handled\":true");
        consoleErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task Render_WhenCesiumReportsShaderCompileFailure_ShouldDisposeAndFallBackWithoutThrowing()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;

        (await InitAsync(page)).Should().BeNull();
        (await page.EvaluateAsync<bool>("""
            () => window.tacticalGlobe.reportRenderError(
                'tg-viewport',
                new Error('Fragment shader failed to compile. Compile log: deferred test'))
            """)).Should().BeTrue();
        await page.WaitForFunctionAsync(
            "document.getElementById('tg-viewport').dataset.globeFallback === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 5000 });

        (await page.EvaluateAsync<string>("document.getElementById('tg-viewport').textContent"))
            .Should().Contain("3D globe unavailable on this GPU/browser");
        (await page.EvaluateAsync<bool>("() => window.tacticalGlobe.whenDrawn('tg-viewport', 10)"))
            .Should().BeFalse("the failed Viewer must be removed from the live viewer registry");
        consoleErrors.Should().BeEmpty();
    }

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

    // Blazor can run this page's <script> tag a second time when the circuit takes over (see the
    // guard at the top of tactical-globe.js). The second run must be silent: a top-level const
    // threw "already declared" on every Overwatch visit until 12-11-26.
    [Fact]
    public async Task Script_ShouldRunASecondTimeWithoutAnError()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);

        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Url = "http://localhost/js/tactical-globe.js" });
        await page.WaitForTimeoutAsync(300);

        pageErrors.Should().BeEmpty();
        consoleErrors.Should().BeEmpty();
    }

    // Mac app (12-11-26): the native map sits under the page at the container's rectangle. It was
    // sent once at init, so after the container moved (sidebar, window resize) clicks landed ~30 pt
    // from the pins drawn there. Now a move is sent as 'layout', and always before a click.
    [Fact]
    public async Task MacApp_ShouldMoveTheNativeMapWithItsContainer_BeforeAClickIsMeasured()
    {
        var (page, _) = await OpenHarnessAsync(fixture.Browser, macApp: true);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();
        await page.EvaluateAsync("() => { window.__nativeMessages = []; }");

        // The container moves right, as when the sidebar opens, and the click follows at once,
        // before any timer: the frame must still arrive first.
        await page.EvaluateAsync("""
            () => {
                const container = document.getElementById('tg-viewport');
                container.style.left = '272px';
                container.dispatchEvent(new MouseEvent('click', { clientX: 400, clientY: 300, bubbles: true }));
            }
            """);
        var json = await page.EvaluateAsync<string>("() => JSON.stringify(window.__nativeMessages.map(m => ({ operation: m.operation, args: Array.from(Object.values(m.arguments)) })))");
        var messages = JsonDocument.Parse(json).RootElement.EnumerateArray().Select(m => m.Clone()).ToList();
        messages.Select(m => m.GetProperty("operation").GetString()).Should().ContainInOrder("layout", "mapTap");
        // The last frame sent before the click is the one the click is measured against (a slow runner
        // can still deliver a setup-time frame first; CI failed on f68e32f with left 0).
        var tapIndex = messages.FindIndex(m => m.GetProperty("operation").GetString() == "mapTap");
        var layout = messages.Take(tapIndex).Last(m => m.GetProperty("operation").GetString() == "layout").GetProperty("args");
        layout[0].GetDouble().Should().Be(272, "the container's new left edge");
        var tap = messages.First(m => m.GetProperty("operation").GetString() == "mapTap").GetProperty("args");
        tap[0].GetDouble().Should().BeApproximately((400 - 272) / layout[2].GetDouble(), 0.001, "measured against the same rectangle");
    }

    // Mac app (12-11-26): a click with a pixel or two of trackpad jitter is still a click, a drag
    // pans once per frame, and wheel ticks become one smooth zoom instead of a queue of 2x jumps.
    [Fact]
    public async Task MacApp_ShouldKeepJitteryClicks_BatchPans_AndZoomSmoothly()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser, macApp: true);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        async Task<JsonElement[]> TakeAsync(string operation)
        {
            await page.WaitForTimeoutAsync(150);
            var json = await page.EvaluateAsync<string>("""
                operation => {
                    const taken = window.__nativeMessages.filter(m => m.operation === operation);
                    window.__nativeMessages = window.__nativeMessages.filter(m => m.operation !== operation);
                    return JSON.stringify(taken.map(m => m.arguments));
                }
                """, operation);
            return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(args => args.Clone()).ToArray();
        }
        async Task ResetAsync() => await page.EvaluateAsync("() => { window.__nativeMessages = []; }");

        (await TakeAsync("init")).Should().ContainSingle();

        await ResetAsync();
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(402, 301);
        await page.Mouse.UpAsync();
        (await TakeAsync("pan")).Should().BeEmpty("2px of jitter isn't a drag");
        var taps = await TakeAsync("mapTap");
        taps.Should().ContainSingle();
        taps[0][0].GetDouble().Should().BeApproximately(402 / 800.0, 0.01);

        await ResetAsync();
        await page.Mouse.MoveAsync(400, 300);
        await page.Mouse.DownAsync();
        // A fast trackpad delivers several moves per frame: 20 in one burst here.
        await page.EvaluateAsync("""
            () => {
                const container = document.getElementById('tg-viewport');
                for (let i = 1; i <= 20; i++) {
                    container.dispatchEvent(new PointerEvent('pointermove', { pointerId: 1, clientX: 400 + i * 5, clientY: 300, bubbles: true }));
                }
            }
            """);
        await page.Mouse.MoveAsync(500, 300);
        await page.Mouse.UpAsync();
        var pans = await TakeAsync("pan");
        pans.Should().HaveCountLessThanOrEqualTo(2, "moves within a frame are summed into one message");
        pans.Sum(p => p[0].GetDouble()).Should().BeApproximately(100 / 800.0, 0.002, "the whole drag is applied, threshold included");
        (await TakeAsync("mapTap")).Should().BeEmpty("releasing a drag isn't a click");

        await ResetAsync();
        // One real tick, then a trackpad-style burst of four within a frame.
        await page.Mouse.MoveAsync(200, 150);
        await page.Mouse.WheelAsync(0, 100);
        await page.EvaluateAsync("""
            () => {
                const container = document.getElementById('tg-viewport');
                for (let i = 0; i < 4; i++) {
                    container.dispatchEvent(new WheelEvent('wheel', { deltaY: 100, clientX: 200, clientY: 150, bubbles: true, cancelable: true }));
                }
            }
            """);
        var zooms = await TakeAsync("zoomBy");
        zooms.Should().HaveCountLessThanOrEqualTo(3, "a burst is one zoom");
        zooms.Aggregate(1.0, (product, z) => product * z[0].GetDouble()).Should().BeApproximately(Math.Exp(500 * 0.0025), 0.03);
        zooms[^1][1].GetDouble().Should().BeApproximately(0.25, 0.01, "zoom centres on the pointer");
        (await TakeAsync("zoom")).Should().BeEmpty();
        consoleErrors.Should().BeEmpty();
    }

    // Unit 3 (06-11-26): FAA airport pins, and the aurora as two imagery layers that a clear removes.
    [Fact]
    public async Task SkyLayers_ShouldDrawAirportsAndTheAurora_AndClearThem()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        var counts = await page.EvaluateAsync<int[]>("""
            async () => {
                // Counts the imagery layers added and removed from here on (the basemap came before).
                let live = 0;
                const add = Cesium.ImageryLayerCollection.prototype.addImageryProvider;
                Cesium.ImageryLayerCollection.prototype.addImageryProvider = function (provider, index) { live++; return add.call(this, provider, index); };
                const remove = Cesium.ImageryLayerCollection.prototype.remove;
                Cesium.ImageryLayerCollection.prototype.remove = function (layer, destroy) { live--; return remove.call(this, layer, destroy); };
                window.tacticalGlobe.setAirportStatus([
                    { id: 'faa-JFK', kind: 'airport', title: 'JFK (New York): ground stop', detail: 'Ground stop (thunderstorms)', severity: 'major', url: 'https://nasstatus.faa.gov/', lat: 40.64, lon: -73.78 }
                ]);
                const rows = 51, columns = 360;
                const band = new Uint8Array(rows * columns);
                for (let c = 0; c < columns; c++) { band[20 * columns + c] = 30; band[21 * columns + c] = 8; }
                let binary = '';
                band.forEach(function (b) { binary += String.fromCharCode(b); });
                const base64 = btoa(binary);
                await window.tacticalGlobe.setAurora({ rows, columns, edge: 40, north: base64, south: base64 });
                const drawn = live;
                await window.tacticalGlobe.setAurora({ rows, columns, edge: 40, north: base64, south: base64 });
                const redrawn = live;
                window.tacticalGlobe.clearAurora();
                const cleared = live;
                window.tacticalGlobe.clearAirportStatus();
                // A forecast with no aurora anywhere draws nothing.
                const quiet = btoa(String.fromCharCode.apply(null, Array.from(new Uint8Array(rows * columns))));
                await window.tacticalGlobe.setAurora({ rows, columns, edge: 40, north: quiet, south: quiet });
                return [drawn, redrawn, cleared, live];
            }
            """);
        await page.WaitForTimeoutAsync(300);

        counts.Should().Equal([2, 2, 0, 0], "one layer per polar band, replaced (not stacked) on a redraw, and gone after a clear");
        consoleErrors.Should().BeEmpty();
    }

    // Unit 5 (06-11-26): satellites at their real height, a station's next orbit, and its window.
    [Fact]
    public async Task Satellites_ShouldDrawAtTheirHeight_AndOpenAStationsWindow()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        // The globe opens centred on 39.8, -98.5, from far enough out that a station there sits mid-canvas.
        var error = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    window.tacticalGlobe.setSatellites([
                        { id: '25544', name: 'ISS (ZARYA)', group: 'space station', lat: 39.8, lon: -98.5, altitudeKm: 415, periodMinutes: 92.9, with: ['POISK'],
                          path: [-98.5, 39.8, 415000, -95, 42, 415000, -90, 44, 416000] },
                        { id: '20580', name: 'HST', group: 'visible', lat: 10, lon: 20, altitudeKm: 530, periodMinutes: 95, with: [], path: [] }
                    ]);
                    return null;
                } catch (e) { return String(e && e.stack || e); }
            }
            """);
        error.Should().BeNull();
        await page.WaitForTimeoutAsync(800);
        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForSelectorAsync(".tg-incident-panel:has-text('ISS (ZARYA)')", new PageWaitForSelectorOptions { Timeout = 5000 });
        var panel = await page.InnerTextAsync(".tg-incident-panel");
        panel.Should().Contain("415 KM").And.Contain("93 MIN").And.Contain("POISK").And.Contain("next orbit");

        await page.ClickAsync(".tg-incident-panel .tg-stream-panel-close");
        await page.EvaluateAsync("() => window.tacticalGlobe.clearSatellites()");
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
    public async Task ThreeD_WithoutAToken_ShouldShowKeylessTerrain_AndTheStreetLevelFlightShouldFinish()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        // The server's terrain proxy, answered here with a 1 x 1 Terrarium tile at sea level.
        var terrainTiles = 0;
        await page.RouteAsync("**/admin/api/overwatch/terrain/**", route =>
        {
            Interlocked.Increment(ref terrainTiles);
            return route.FulfillAsync(new() { ContentType = "image/png",
                BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNoYGAAAAGEAIH5/mWIAAAAAElFTkSuQmCC") });
        });
        (await InitAsync(page)).Should().BeNull();

        // No ion token (13-11-26): terrain only, from the open elevation tiles through the server.
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', true)")).Should().Be("on");
        var flight = await page.EvaluateAsync<double>("""
            async () => {
                const started = performance.now();
                await window.tacticalGlobe.flyToStreetLevel('tg-viewport', 33.75, -84.39, 90);
                return performance.now() - started;
            }
            """);
        flight.Should().BeLessThan(8000, "street view waits for this flight, so it must always finish");
        terrainTiles.Should().BePositive("terrain comes from the server's proxy");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', false)")).Should().Be("off");
        consoleErrors.Should().BeEmpty();
    }

    // Camera pins by difference (13-11-26): only changes cross the network, the globe keeps the set.
    [Fact]
    public async Task CameraPins_ShouldMergeDifferences_AndHandTheWholeSetToTheMap()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser, macApp: true);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();
        await page.EvaluateAsync("() => { window.__nativeMessages = []; }");

        var counts = await page.EvaluateAsync<int[]>("""
            () => {
                const pin = (id) => ({ id, name: id, lat: 33.7, lon: -84.4, streamUrl: 'https://x/' + id, streamKind: 'Snapshot', sourceName: 's', health: 'Unknown' });
                const sent = () => window.__nativeMessages.filter(m => m.operation === 'setCameraPins');
                window.tacticalGlobe.updateCameraPins([pin('a'), pin('b')], []);
                window.tacticalGlobe.updateCameraPins([pin('c')], ['a']);
                window.tacticalGlobe.resetCameraPins();
                window.tacticalGlobe.updateCameraPins([pin('d')], []);
                return sent().map(m => Array.from(Object.values(m.arguments)).find(Array.isArray).length);
            }
            """);

        counts.Should().Equal([2, 2, 1], "a+b, then b+c, then only d after a reset");
        consoleErrors.Should().BeEmpty();
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
    public async Task OverlappingDecorativeMarker_ShouldNotPreventTheActionablePinFromOpening()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser);
        await using var pageScope = page;
        (await InitAsync(page)).Should().BeNull();

        await page.EvaluateAsync("""
            () => {
              window.tacticalGlobe.setTrafficIncidents([{
                id: 'incident-under-marker', roadwayName: 'US 36', description: 'Closure beneath another marker.',
                eventType: 'closures', severity: 'major', lat: 39.8, lon: -98.5,
                sourceName: 'Test feed', sourceAttributionUrl: 'https://example.test/incidents'
              }]);
              // Added second at the same pixel so scene.pick can return this non-actionable
              // decorative marker before the incident below it.
              window.tacticalGlobe.setStreetViewMarker('tg-viewport', 39.8, -98.5);
            }
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-incident-panel')",
            new PageWaitForFunctionOptions { Timeout = 5000 });

        (await page.InnerTextAsync(".tg-incident-panel"))
            .Should().Contain("US 36").And.Contain("Closure beneath another marker");
        consoleErrors.Should().BeEmpty();
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
              href: '/admin/world-news?situation=situation-1', eventId: 'event 1/a',
              sources: [{ outlet: 'Example News', title: 'Rail services delayed', url: 'https://example.test/report' }]
            }])
            """);
        await page.WaitForTimeoutAsync(1000);

        await page.Mouse.ClickAsync(400, 300);
        await page.WaitForFunctionAsync("!!document.querySelector('.tg-incident-panel')", new PageWaitForFunctionOptions { Timeout = 5000 });

        var panelText = await page.EvaluateAsync<string>("document.querySelector('.tg-incident-panel').textContent");
        panelText.Should().Contain("REPORTED NEWS").And.Contain("NOT A CONFIRMED INCIDENT")
            .And.Contain("United States rail disruption").And.Contain("12 OUTLETS");
        (await page.GetAttributeAsync(".tg-incident-panel a.tg-incident-report:not(.tg-world-news-document)", "href"))
            .Should().Be("/admin/world-news?situation=situation-1");
        // READ DOCUMENT opens the latest event's story page (06-11-26).
        (await page.GetAttributeAsync(".tg-incident-panel a.tg-world-news-document", "href"))
            .Should().Be("/admin/world-news/events/event%201%2Fa");
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

        var worldTiles = 0;
        // RainViewer frame paths expire after ~2 hours, so a stand-in tile answers; what matters is
        // that the globe asks for world tiles and the CSP lets them through (12-11-26).
        await page.RouteAsync("https://tilecache.rainviewer.com/**", route =>
        {
            Interlocked.Increment(ref worldTiles);
            return route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==") });
        });

        await page.EvaluateAsync(RadarTimelineScript);
        await page.EvaluateAsync("() => window.tacticalGlobe.setRadarVisible(true)");
        await page.WaitForFunctionAsync("document.querySelector('.tg-radar-bar') && document.querySelector('.tg-radar-bar').style.display === 'block'", new PageWaitForFunctionOptions { Timeout = 5000 });

        (await page.TextContentAsync(".tg-radar-kind")).Should().Be("LATEST RADAR · WORLDWIDE");
        for (var i = 0; i < 50 && Volatile.Read(ref worldTiles) == 0; i++) await page.WaitForTimeoutAsync(100);
        worldTiles.Should().BeGreaterThan(0, "the worldwide layer draws under the US one");
        (await page.InputValueAsync(".tg-radar-slider")).Should().Be("2");

        await page.ClickAsync("[data-radar='forward']");
        (await page.TextContentAsync(".tg-radar-kind")).Should().StartWith("FUTURE RADAR · HRRR MODEL");
        (await page.TextContentAsync(".tg-radar-kind")).Should().EndWith("US ONLY");
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

}
