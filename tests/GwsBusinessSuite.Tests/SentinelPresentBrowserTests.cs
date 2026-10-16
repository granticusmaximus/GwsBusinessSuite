using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 18: js/sentinelPresent.js on the markup SentinelPresent.razor renders.
[Collection("Playwright")]
public sealed class SentinelPresentBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private const string Markup = """
        <!DOCTYPE html><html><head><base href="/" /><script src="/js/sentinelPresent.js"></script><style>body{margin:0}.sentinel-present{display:block;min-height:100vh}</style></head><body>
        <main class="sentinel-present" data-present-exit="/admin/sentinel?page=abc" data-present-key="abc">
          <section class="sentinel-slide is-title" data-slide="1"><h1>Title</h1></section>
          <section class="sentinel-slide" data-slide="2" hidden><h2>Second</h2><a href="/elsewhere" id="link">a link</a></section>
          <section class="sentinel-slide" data-slide="3" hidden><h2>Third</h2></section>
          <nav class="sentinel-present-controls">
            <button type="button" data-present="prev">prev</button>
            <span class="sentinel-present-counter">1 / 3</span>
            <button type="button" data-present="next">next</button>
            <a href="/admin/sentinel?page=abc" data-present="exit">exit</a>
          </nav>
          <aside class="sentinel-present-speaker" hidden>
            <div><strong class="sentinel-present-speaker-counter"></strong><span class="sentinel-present-speaker-timer">00:00</span></div>
            <div class="sentinel-present-speaker-next"><div class="sentinel-present-speaker-next-slide"></div></div>
          </aside>
        </main></body></html>
        """;

    private async Task<IPage> OpenAsync(IBrowserContext context, string path)
    {
        var page = await context.NewPageAsync();
        await page.RouteAsync("http://localhost/admin/sentinel/present/abc*", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = Markup }));
        await page.RouteAsync("http://localhost/js/sentinelPresent.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Path = Path.Combine(WebRoot, "js", "sentinelPresent.js")
        }));
        await page.RouteAsync(url => url == "http://localhost/admin/sentinel?page=abc", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<h1>Back in Sentinel</h1>" }));
        await page.GotoAsync("http://localhost" + path);
        return page;
    }

    private static ILocator Visible(IPage page) => page.Locator(".sentinel-slide:not([hidden]) :is(h1,h2)");

    [Fact]
    public async Task Keys_Clicks_AndTheAddress_ShouldMoveBetweenSlides_AndEscapeShouldExit()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1200, Height = 800 } });
        var page = await OpenAsync(context, "/admin/sentinel/present/abc");

        await Expect(Visible(page)).ToHaveTextAsync("Title");
        await page.Keyboard.PressAsync("ArrowRight");
        await Expect(Visible(page)).ToHaveTextAsync("Second");
        await Expect(page.Locator(".sentinel-present-counter")).ToHaveTextAsync("2 / 3");
        page.Url.Should().Be("http://localhost/admin/sentinel/present/abc#2", "the app's <base href=\"/\"> must not turn it into /#2");
        await page.Keyboard.PressAsync(" ");
        await page.Keyboard.PressAsync("ArrowRight");
        await Expect(Visible(page)).ToHaveTextAsync("Third", new() { Timeout = 2000 });
        await page.Keyboard.PressAsync("Home");
        await Expect(Visible(page)).ToHaveTextAsync("Title");
        await page.Keyboard.PressAsync("End");
        await Expect(Visible(page)).ToHaveTextAsync("Third");

        // Left third goes back, the rest goes forward; the controls work too.
        await page.Mouse.ClickAsync(100, 400);
        await Expect(Visible(page)).ToHaveTextAsync("Second");
        await page.ClickAsync("[data-present=prev]");
        await Expect(Visible(page)).ToHaveTextAsync("Title");
        await page.Mouse.ClickAsync(900, 400);
        await Expect(Visible(page)).ToHaveTextAsync("Second");

        // A reload stays on the slide in the address.
        await page.ReloadAsync();
        await Expect(Visible(page)).ToHaveTextAsync("Second");

        await page.Keyboard.PressAsync("Escape");
        await Expect(page).ToHaveURLAsync("http://localhost/admin/sentinel?page=abc");
        await Expect(page.Locator("h1")).ToHaveTextAsync("Back in Sentinel");
    }

    [Fact]
    public async Task SpeakerView_ShouldFollowTheMainWindow_AndShowTheNextSlide()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var main = await OpenAsync(context, "/admin/sentinel/present/abc");
        var speaker = await OpenAsync(context, "/admin/sentinel/present/abc?speaker=1#1");

        await Expect(speaker.Locator(".sentinel-present-speaker")).ToBeVisibleAsync();
        await Expect(speaker.Locator(".sentinel-present-speaker-next-slide h2")).ToHaveTextAsync("Second");
        await main.Keyboard.PressAsync("ArrowRight");
        await Expect(speaker.Locator(".sentinel-present-speaker-counter")).ToHaveTextAsync("Slide 2 of 3");
        await Expect(speaker.Locator(".sentinel-present-speaker-next-slide h2")).ToHaveTextAsync("Third");

        // And the other way: the presenter can drive from the speaker window.
        await speaker.Keyboard.PressAsync("ArrowRight");
        await Expect(Visible(main)).ToHaveTextAsync("Third");
        await Expect(speaker.Locator(".sentinel-present-speaker-next-slide")).ToHaveTextAsync("End of the presentation");
    }
}
