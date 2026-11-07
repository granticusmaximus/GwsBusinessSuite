using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// The workspace rail, icons only (07-11-26: Grant found the labels under its icons clunky and
// crowded in the installed app on his iPad, and wanted the Mac app streamlined too). Each label is
// still there for screen readers, and every item is still a full touch target inside the rail.
[Collection("Playwright")]
public sealed class WorkspaceRailLayoutBrowserTests(PlaywrightBrowserFixture fixture)
{
    // The app's own stylesheets, in the order App.razor loads them (Bootstrap's sets the box sizing).
    private static string AppCss => string.Concat(new[] { "lib/bootstrap/dist/css/bootstrap.min.css", "app.css", "css/gws-responsive.css" }.Select(file =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot", file)))));

    private static readonly string[] Workspaces = ["Home", "Content", "Relations", "Knowledge", "Intel"];

    // The rail as NavMenu.razor and RailShortcuts.razor draw it: a button per workspace, then
    // Recent and Settings at the bottom.
    private static string Rail() => $"""
        <html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body>
        <nav class="gws-nav" aria-label="Primary">
          <div class="gws-rail" role="group" aria-label="Workspaces">
            {string.Concat(Workspaces.Select(name =>
                $"""<button type="button" class="gws-rail-item" title="{name} workspace"><i class="bi bi-grid" aria-hidden="true"></i><span>{name}</span></button>"""))}
            <div class="gws-rail-bottom">
              <button type="button" class="gws-rail-item" title="Pinned pages and recent records"><i class="bi bi-clock-history" aria-hidden="true"></i><span>Recent</span></button>
              <a class="gws-rail-item" href="#settings" title="Settings"><i class="bi bi-gear-fill" aria-hidden="true"></i><span>Settings</span></a>
            </div>
          </div>
        </nav>
        </body></html>
        """;

    [Theory]
    [InlineData(820, 1180, true)] // the installed app on an iPad, upright
    [InlineData(1440, 900, false)] // the Mac app's window
    public async Task Rail_ShouldShowIconsOnly_AndKeepEachLabelForScreenReaders(int width, int height, bool touch)
    {
        await using var context = await fixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height }, HasTouch = touch, IsMobile = touch
        });
        var page = await context.NewPageAsync();
        await page.SetContentAsync(Rail());
        await page.AddStyleTagAsync(new() { Content = AppCss });

        var measured = await page.EvaluateAsync<JsonElement>("""
            () => {
                const rail = document.querySelector('.gws-rail').getBoundingClientRect();
                const items = [...document.querySelectorAll('.gws-rail-item')].map(item => {
                    const box = item.getBoundingClientRect();
                    const label = item.querySelector(':scope > span').getBoundingClientRect();
                    return { width: box.width, height: box.height, left: box.left - rail.left, right: rail.right - box.right, labelWidth: label.width, labelHeight: label.height };
                });
                return { width: rail.width, items };
            }
            """);

        measured.GetProperty("width").GetDouble().Should().Be(60, "the rail is 60px without labels");
        var items = measured.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(Workspaces.Length + 2);
        foreach (var item in items)
        {
            item.GetProperty("labelWidth").GetDouble().Should().BeLessThanOrEqualTo(1, "no label shows under the icon");
            item.GetProperty("labelHeight").GetDouble().Should().BeLessThanOrEqualTo(1);
            item.GetProperty("height").GetDouble().Should().BeInRange(44, 52, "a full touch target, no taller than an icon needs");
            item.GetProperty("left").GetDouble().Should().BeGreaterThanOrEqualTo(0, "it fits inside the rail");
            item.GetProperty("right").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        }

        foreach (var name in Workspaces.Append("Recent"))
            (await page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).CountAsync()).Should().Be(1, $"{name} keeps its name for screen readers");
        (await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).CountAsync()).Should().Be(1);
    }
}
