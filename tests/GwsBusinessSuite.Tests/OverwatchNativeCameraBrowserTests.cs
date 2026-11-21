using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;
using Microsoft.Playwright;
using static GwsBusinessSuite.Tests.OverwatchGridScriptBrowserTests;

namespace GwsBusinessSuite.Tests;

// Night Ops phase 6 (20-11-26): on the Mac's Apple map, the page's stage chip, HUD readout and night
// mode follow the camera reports the map sends (MacNativeOverwatchBridge.ReportCamera), by the same
// rules as the web globe (OverwatchMapLook.Stage, SunPosition, Night).
[Collection("PlaywrightOverwatchB")]
public sealed class OverwatchNativeCameraBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppleMapCameraReports_ShouldSetStageReadoutAndNight_AndHideTerrainShading()
    {
        var (page, consoleErrors) = await OpenHarnessAsync(fixture.Browser, macApp: true, nativeMap: true);
        await using var pageScope = page;
        await page.Clock.SetFixedTimeAsync(Noon.UtcDateTime);
        await page.EvaluateAsync("""
            () => document.querySelector('.tg-shell').insertAdjacentHTML('afterbegin',
                '<div class="tg-stage"><span id="tg-stage-name">ORBIT</span></div><div id="tg-readout"></div>')
            """);
        (await InitAsync(page)).Should().BeNull();

        foreach (var (altitude, lon, pitch, heading) in new[] { (9_000_000d, 0d, 0d, 0d), (400_000d, -97d, 0d, 12d), (20_000d, 180d, 30d, 271.6d), (1_200d, 0d, 55d, 359.7d) })
        {
            var shown = await page.EvaluateAsync<string[]>($$"""
                () => {
                    window.gwsNativeOverwatch.camera({ altitude: {{altitude}}, heading: {{heading}}, pitch: {{pitch}}, lat: 10, lon: {{lon}} });
                    const shell = document.querySelector('.tg-shell');
                    return [shell.dataset.owStage, document.getElementById('tg-stage-name').textContent, document.getElementById('tg-readout').textContent, shell.dataset.owNight];
                }
                """);
            var stage = OverwatchMapLook.Stage(altitude);
            shown[0].Should().Be(stage);
            shown[1].Should().Be(stage.ToUpperInvariant());
            var sun = OverwatchMapLook.SunPosition(Noon, 10, lon);
            shown[3].Should().Be(OverwatchMapLook.Night(sun.Elevation));
            shown[2].Should().StartWith($"HDG {Math.Round((heading + 360) % 360) % 360:000}°")
                .And.Contain("ALT ").And.Contain("10.000 N").And.Contain("SUN ").And.NotContain("GND");
            if (pitch > 5) shown[2].Should().Contain($"TILT {pitch}°");
            else shown[2].Should().NotContain("TILT");
        }
        (await page.EvaluateAsync<string>("() => document.querySelector('.tg-shell').dataset.owNight")).Should().Be("day");

        // A broken report changes nothing.
        await page.EvaluateAsync("() => window.gwsNativeOverwatch.camera({ altitude: NaN, heading: 0, pitch: 0, lat: 0, lon: 0 })");
        (await page.EvaluateAsync<string>("() => document.querySelector('.tg-shell').dataset.owStage")).Should().Be("ground");

        // 3D on the Apple map answers 'apple': on, but no contours or slope buttons on the page.
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', true)")).Should().Be("apple");
        (await page.EvaluateAsync<string>("() => window.tacticalGlobe.setThreeD('tg-viewport', false)")).Should().Be("off");
        consoleErrors.Should().BeEmpty();
    }
}
