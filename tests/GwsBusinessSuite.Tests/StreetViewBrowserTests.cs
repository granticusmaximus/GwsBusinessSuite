using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Overwatch's own street view (wwwroot/js/street-view.js), driven in a real browser with a
// generated test panorama whose colour tells you which way you're looking:
//   relative to the photo's middle: -180..-90 red, -90..0 yellow, 0..90 green, 90..180 blue,
//   and the top tenth (the sky) white.
[Collection("Playwright")]
public sealed class StreetViewBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string Html = """
        <!doctype html>
        <html><head>
          <script src="/js/street-view.js"></script>
          <style>body { margin: 0; } #tg-sv { position: relative; width: 640px; height: 400px; }
            .tg-sv-canvas { position: absolute; inset: 0; width: 100%; height: 100%; display: block; }
            .tg-sv-arrows { position: absolute; inset: 0; pointer-events: none; }
            .tg-sv-arrow { position: absolute; width: 40px; height: 40px; pointer-events: auto; }
            .tg-sv-compass { position: absolute; left: 8px; bottom: 8px; width: 40px; height: 40px; }</style>
        </head><body>
          <div id="tg-sv"><canvas class="tg-sv-canvas" tabindex="0"></canvas><div class="tg-sv-arrows"></div>
            <div class="tg-sv-compass"><div class="tg-sv-compass-needle">N</div></div><div class="tg-sv-status" hidden></div></div>
        </body></html>
        """;

    private const string MakePanorama = """
        () => {
          const canvas = document.createElement('canvas');
          canvas.width = 512; canvas.height = 256;
          const ctx = canvas.getContext('2d');
          ['#ff0000', '#ffff00', '#00ff00', '#0000ff'].forEach((color, i) => { ctx.fillStyle = color; ctx.fillRect(i * 128, 0, 128, 256); });
          ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, 512, 26);
          return canvas.toDataURL('image/png');
        }
        """;

    private async Task<IPage> OpenAsync(double compassAngle)
    {
        var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 800, Height = 600 } });
        var script = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/street-view.js")));
        await page.RouteAsync("http://localhost/sv", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            // The same rules the app serves: own scripts only, images same-origin/data.
            Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; img-src 'self' data:; style-src 'unsafe-inline'" },
            Body = Html
        }));
        await page.RouteAsync("http://localhost/js/street-view.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = script
        }));
        await page.GotoAsync("http://localhost/sv");
        var panorama = await page.EvaluateAsync<string>(MakePanorama);
        await page.EvaluateAsync("""
            ([panorama, compass]) => {
              window.calls = [];
              window.panorama = panorama;
              const dotNet = { invokeMethodAsync: (method, ...args) => {
                window.calls.push([method, ...args]);
                if (method === 'MoveStreetViewAsync') return Promise.resolve({
                  current: { id: args[0], latitude: 33.7491, longitude: -84.388, compassAngle: compass },
                  links: [{ id: '1001', bearing: 180, distanceMeters: 11 }], imageUrl: window.panorama });
                return Promise.resolve(null);
              } };
              return window.gwsStreetView.open('tg-sv', dotNet, {
                current: { id: '1001', latitude: 33.749, longitude: -84.388, compassAngle: compass },
                links: [{ id: '1002', bearing: 0, distanceMeters: 11 }, { id: '1003', bearing: 90, distanceMeters: 15 }],
                imageUrl: panorama, initialHeading: 45 });
            }
            """, new object[] { panorama, compassAngle });
        await page.WaitForFunctionAsync("() => window.gwsStreetView.getState()?.loadedId === '1001'");
        return page;
    }

    // The colour of the pixel in the middle of the view, as "r,g,b".
    private static async Task<string> CentreColourAsync(IPage page)
    {
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        return await page.EvaluateAsync<string>("""
            () => {
              const canvas = document.querySelector('.tg-sv-canvas');
              const gl = canvas.getContext('webgl');
              const pixel = new Uint8Array(4);
              gl.readPixels(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
              return `${pixel[0]},${pixel[1]},${pixel[2]}`;
            }
            """);
    }

    [Fact]
    public async Task LookingAround_ShouldShowTheRightPartOfThePanorama_LinedUpWithNorth()
    {
        await using var _ = await OpenAsync(compassAngle: 0) is var page ? new PageScope(page) : null!;

        (await CentreColourAsync(page)).Should().Be("0,255,0", "45° east of the photo's middle is the green quarter");
        await page.EvaluateAsync("() => window.gwsStreetView.look(135, 0)");
        (await CentreColourAsync(page)).Should().Be("0,0,255");
        await page.EvaluateAsync("() => window.gwsStreetView.look(-45, 0)");
        (await CentreColourAsync(page)).Should().Be("255,255,0");
        await page.EvaluateAsync("() => window.gwsStreetView.look(225, 0)");
        (await CentreColourAsync(page)).Should().Be("255,0,0");
        await page.EvaluateAsync("() => window.gwsStreetView.look(45, 80)");
        (await CentreColourAsync(page)).Should().Be("255,255,255", "looking up shows the sky");
    }

    [Fact]
    public async Task ThePhotosCompassAngle_ShouldRotateItIntoPlace()
    {
        // Same photo, but its middle faces east: looking north-east (45) is now 45° LEFT of the middle.
        await using var _ = await OpenAsync(compassAngle: 90) is var page ? new PageScope(page) : null!;

        (await CentreColourAsync(page)).Should().Be("255,255,0");
        await page.EvaluateAsync("() => window.gwsStreetView.look(135, 0)");
        (await CentreColourAsync(page)).Should().Be("0,255,0");
    }

    [Fact]
    public async Task Dragging_ShouldTurn_AndArrowsShouldPointAtTheirNeighbours()
    {
        await using var _ = await OpenAsync(compassAngle: 0) is var page ? new PageScope(page) : null!;
        await page.EvaluateAsync("() => window.gwsStreetView.look(0, 0)");
        await CentreColourAsync(page);

        var links = await page.EvaluateAsync<JsonElement>("() => window.gwsStreetView.getState().links");
        links.EnumerateArray().Select(link => (link.GetProperty("id").GetString(), link.GetProperty("relative").GetInt32()))
            .Should().Equal(("1002", 0), ("1003", 90));

        // Dragging the picture to the left turns you to the right (east).
        await page.Mouse.MoveAsync(400, 200);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(200, 200, new() { Steps = 5 });
        await page.Mouse.UpAsync();
        var yaw = await page.EvaluateAsync<double>("() => window.gwsStreetView.getState().yaw");
        yaw.Should().BeApproximately(80 * 200 / 400.0, 1, "80° of view across 400 px");
    }

    [Fact]
    public async Task ClickingAnArrow_ShouldWalkThere_KeepingTheDirectionYouFace()
    {
        await using var _ = await OpenAsync(compassAngle: 0) is var page ? new PageScope(page) : null!;
        await page.EvaluateAsync("() => window.gwsStreetView.look(90, 10)");
        await CentreColourAsync(page);

        await page.Locator(".tg-sv-arrow[data-panorama-id='1003']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.gwsStreetView.getState().panoramaId === '1003'");

        var calls = await page.EvaluateAsync<JsonElement>("() => window.calls");
        calls.EnumerateArray().Should().ContainSingle(call => call[0].GetString() == "MoveStreetViewAsync" && call[1].GetString() == "1003");
        var state = await page.EvaluateAsync<JsonElement>("() => window.gwsStreetView.getState()");
        state.GetProperty("yaw").GetDouble().Should().Be(90);
        state.GetProperty("links").EnumerateArray().Should().ContainSingle(link => link.GetProperty("id").GetString() == "1001");

        await page.Locator(".tg-sv-canvas").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForFunctionAsync("() => window.calls.some(call => call[0] === 'CloseStreetViewAsync')");
    }

    private sealed class PageScope(IPage page) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(page.CloseAsync());
    }
}
