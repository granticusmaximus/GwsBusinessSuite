using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// 19-11-26: the offline field kit is one self-contained page: pictures built in, no inline script
// (CSP), and the service worker shows a kept kit with no signal.
public sealed class FieldKitTests
{
    [Fact]
    public void Page_ShouldBuildEverythingIn_AndEscapeWhatItShows()
    {
        var area = new OverwatchAreaView(Guid.NewGuid(), "Midtown <Atlanta>", new BoundingBox(33.8, 33.7, -84.3, -84.4), true, true, true, "minor", null);
        var camera = new CameraFeed("c1", "I-75 @ 10th", 33.78, -84.38, "https://cams.example/1.jpg", CameraStreamKind.Snapshot, "GDOT", "https://511ga.org");
        var html = FieldKitService.Render(area, [(camera, new byte[] { 0xFF, 0xD8, 0xFF })], [new WeatherAlert("a", "Flood Warning", "Severe", "Fulton", "", [])],
            [new HazardFeature("h", "flood", "River high", "", 33.75, -84.39, "major", "", null)], [], new DateTimeOffset(2026, 11, 19, 20, 0, 0, TimeSpan.Zero));

        html.Should().Contain("Midtown &lt;Atlanta&gt;").And.NotContain("<Atlanta>");
        html.Should().Contain("src=\"data:image/jpeg;base64,/9j/").And.Contain("Flood Warning").And.Contain("River high");
        html.Should().Contain("<script src=\"/js/field-kit.js\"></script>").And.NotContain("<script>", "the CSP blocks inline scripts");
        html.Should().Contain($"data-area=\"{area.Id}\"");
    }

    [Fact]
    public void ServiceWorker_ShouldServeAKeptKitOffline_FromACacheItDoesNotClear()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var sw = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/sw.js"));
        var kit = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/js/field-kit.js"));
        sw.Should().Contain("url.pathname.startsWith('/admin/field-kit/')").And.Contain("url.pathname === '/js/field-kit.js'");
        kit.Should().Contain("const KIT_CACHE = 'field-kits-v1';", "sw.js deletes its own old gws- caches on update");
        kit.Should().Contain("'X-Field-Kit': '1'");
    }
}
