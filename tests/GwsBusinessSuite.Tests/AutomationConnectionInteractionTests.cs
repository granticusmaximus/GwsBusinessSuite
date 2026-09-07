using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Connections used to be unclickable by construction: the whole SVG layer carried
// pointer-events:none, so no amount of markup could have made an edge selectable. These cover the
// two things that make an edge a real object - the layer receiving events, and a hit-stroke wide
// enough to actually land on.
[Collection("Playwright")]
public sealed class AutomationConnectionInteractionTests(PlaywrightBrowserFixture fixture)
{
    private static string EditorCssPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/AutomationEditor.razor.css"));

    [Fact]
    public void ConnectionLayer_MustNotDisablePointerEvents()
    {
        // A cheap guard against the exact regression: re-adding pointer-events:none here silently
        // makes every connection unclickable again, and nothing else would fail.
        var css = File.ReadAllText(EditorCssPath);
        var layerRule = css.Split('\n')
            .FirstOrDefault(line => line.TrimStart().StartsWith(".automation-connections {", StringComparison.Ordinal));

        layerRule.Should().NotBeNull(".automation-connections must still be styled");
        layerRule.Should().NotContain("pointer-events:none",
            "the layer has to receive events for edges to be selectable");
    }

    [Fact]
    public void VisibleConnectionLine_ShouldOptOutOfPointerEvents()
    {
        // The visible line must not steal the pointer from the wider hit-stroke beneath it.
        var css = File.ReadAllText(EditorCssPath);
        css.Should().Contain(".automation-connection { fill:none; stroke:#57606f; stroke-width:2.25; pointer-events:none; }");
        css.Should().Contain(".automation-connection-hit");
    }

    [Fact]
    public async Task TheHitStroke_ShouldBeClickableWhereTheThinLineIsNot()
    {
        // The visible line is 2.25px. Clicking a few pixels off it still has to select the edge,
        // which is the entire reason for the transparent 18px stroke.
        var css = await File.ReadAllTextAsync(EditorCssPath);
        var html = $$"""
            <!doctype html><html><head><style>
              {{css}}
              .automation-canvas { position:relative; width:600px; height:300px; }
            </style></head><body>
            <div class="automation-canvas">
              <svg class="automation-connections" width="600" height="300">
                <g class="automation-connection-group">
                  <path id="hit" d="M 100 150 C 200 150, 300 150, 400 150" class="automation-connection-hit"></path>
                  <path d="M 100 150 C 200 150, 300 150, 400 150" class="automation-connection"></path>
                </g>
              </svg>
            </div>
            <script>
              window.__hits = 0;
              document.getElementById('hit').addEventListener('click', () => { window.__hits++; });
            </script></body></html>
            """;

        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", r => r.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = html
        }));
        await page.GotoAsync("http://localhost/canvas");

        // Measured from the live SVG box: page and SVG coordinates differ by the body margin,
        // which is a large fraction of the tolerance under test.
        var box = await (await page.QuerySelectorAsync("svg.automation-connections"))!.BoundingBoxAsync();
        await page.Mouse.ClickAsync(box!.X + 250, box.Y + 150 - 6);

        var hits = await page.EvaluateAsync<int>("() => window.__hits");
        hits.Should().Be(1, "clicking near the wire must select it, not require pixel precision");
    }

    [Fact]
    public async Task TheVisibleLine_ShouldNotSwallowTheClick()
    {
        // Directly over the line: the hit-stroke must still be what receives the event, because
        // the visible path sits on top of it in paint order.
        var css = await File.ReadAllTextAsync(EditorCssPath);
        var html = $$"""
            <!doctype html><html><head><style>
              {{css}}
              .automation-canvas { position:relative; width:600px; height:300px; }
            </style></head><body>
            <div class="automation-canvas">
              <svg class="automation-connections" width="600" height="300">
                <g class="automation-connection-group">
                  <path id="hit" d="M 100 150 C 200 150, 300 150, 400 150" class="automation-connection-hit"></path>
                  <path d="M 100 150 C 200 150, 300 150, 400 150" class="automation-connection"></path>
                </g>
              </svg>
            </div>
            <script>
              window.__hits = 0;
              document.getElementById('hit').addEventListener('click', () => { window.__hits++; });
            </script></body></html>
            """;

        await using var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", r => r.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = html
        }));
        await page.GotoAsync("http://localhost/canvas");

        var box = await (await page.QuerySelectorAsync("svg.automation-connections"))!.BoundingBoxAsync();
        await page.Mouse.ClickAsync(box!.X + 250, box.Y + 150);

        (await page.EvaluateAsync<int>("() => window.__hits")).Should().Be(1);
    }
}
