using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Drives the real extension popup (extensions/sentinel-web-clipper) in Chromium, with the
// browser-extension APIs stubbed and the Sentinel endpoint intercepted.
[Collection("Playwright")]
public sealed class SentinelWebClipperBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string ExtensionRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../extensions/sentinel-web-clipper"));

    private const string ExtensionStub = """
        window.__stored = { server: "http://localhost", token: "gwsclip_test" };
        globalThis.chrome = {
          storage: { local: {
            get: async keys => Object.fromEntries([].concat(keys).filter(k => k in window.__stored).map(k => [k, window.__stored[k]])),
            set: async values => Object.assign(window.__stored, values)
          } },
          tabs: { query: async () => [{ id: 1, title: "Tab title", url: "https://example.com/post" }] },
          scripting: { executeScript: async () => [{ result: {
            title: "Example post", url: "https://example.com/post",
            html: "<html><body><article><p>Body</p></article></body></html>", selectionHtml: "<p>Picked <b>text</b></p>"
          } }] },
          runtime: { openOptionsPage() {} },
          permissions: { request: async () => true }
        };
        """;

    [Fact]
    public async Task Popup_ShouldPreferTheSelection_AndPostTheClipWithTheToken()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await RouteExtensionAsync(page);
        string? authorization = null;
        JsonElement? body = null;
        await page.RouteAsync("http://localhost/api/sentinel/clip", async route =>
        {
            authorization = route.Request.Headers.GetValueOrDefault("authorization");
            body = JsonDocument.Parse(route.Request.PostData!).RootElement.Clone();
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = """{"id":"00000000-0000-0000-0000-000000000001","title":"Example post","target":"today","url":"http://localhost/admin/sentinel?page=1"}"""
            });
        });
        await page.AddInitScriptAsync(ExtensionStub);

        await page.GotoAsync("http://localhost/ext/popup.html");

        await Expect(page.Locator("#title")).ToHaveValueAsync("Example post");
        await Expect(page.Locator("input[value=selection]")).ToBeCheckedAsync();
        await page.SelectOptionAsync("#target", "today");
        await page.FillAsync("#note", "Keep this");
        await page.FillAsync("#tags", "#research, client/acme");
        await page.ClickAsync("#clip-button");

        await Expect(page.Locator("#status")).ToContainTextAsync("Added to today's daily note");
        await Expect(page.Locator("#status a")).ToHaveAttributeAsync("href", "http://localhost/admin/sentinel?page=1");
        authorization.Should().Be("Bearer gwsclip_test");
        body!.Value.GetProperty("mode").GetString().Should().Be("selection");
        body.Value.GetProperty("html").GetString().Should().Be("<p>Picked <b>text</b></p>");
        body.Value.GetProperty("target").GetString().Should().Be("today");
        body.Value.GetProperty("note").GetString().Should().Be("Keep this");
        body.Value.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("research", "client/acme");
        (await page.EvaluateAsync<string>("() => window.__stored.target")).Should().Be("today");
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Popup_ShouldShowTheServersError_AndAskForSetupWithoutAToken()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await RouteExtensionAsync(page);
        await page.RouteAsync("http://localhost/api/sentinel/clip", route => route.FulfillAsync(new()
        {
            Status = 401, ContentType = "application/json", Body = """{"error":"That clip token isn't valid."}"""
        }));
        await page.AddInitScriptAsync(ExtensionStub);
        await page.GotoAsync("http://localhost/ext/popup.html");
        await page.ClickAsync("input[value=link]");
        await page.ClickAsync("#clip-button");
        await Expect(page.Locator("#status.error")).ToHaveTextAsync("That clip token isn't valid.");

        await using var unset = await fixture.Browser.NewPageAsync();
        await RouteExtensionAsync(unset);
        await unset.AddInitScriptAsync(ExtensionStub + "\nwindow.__stored = {};");
        await unset.GotoAsync("http://localhost/ext/popup.html");
        await Expect(unset.Locator("#setup")).ToBeVisibleAsync();
        await Expect(unset.Locator("#clip")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task CapturePage_ShouldReturnTheSelection_AndStripScriptsFromThePage()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await RouteExtensionAsync(page);
        await page.AddInitScriptAsync(ExtensionStub);
        await page.GotoAsync("http://localhost/ext/popup.html");
        await page.EvaluateAsync("""
            () => {
              const article = document.createElement("article");
              article.innerHTML = "<p id='para'>Pick <em>me</em> please</p><script>window.x = 1<\/script><style>p{}</style>";
              document.body.appendChild(article);
              const range = document.createRange();
              range.selectNodeContents(document.getElementById("para"));
              getSelection().removeAllRanges();
              getSelection().addRange(range);
            }
            """);

        var captured = await page.EvaluateAsync<JsonElement>("() => capturePage()");

        captured.GetProperty("selectionHtml").GetString().Should().Be("Pick <em>me</em> please");
        var html = captured.GetProperty("html").GetString();
        html.Should().Contain("Pick <em>me</em> please").And.NotContain("<script").And.NotContain("<style");
    }

    private static async Task RouteExtensionAsync(IPage page) =>
        await page.RouteAsync("http://localhost/ext/**", async route =>
        {
            var relative = new Uri(route.Request.Url).AbsolutePath["/ext/".Length..];
            var path = Path.GetFullPath(Path.Combine(ExtensionRoot, relative));
            if (!path.StartsWith(ExtensionRoot, StringComparison.Ordinal) || !File.Exists(path))
            {
                await route.FulfillAsync(new() { Status = 404 });
                return;
            }
            var contentType = Path.GetExtension(path) switch
            {
                ".html" => "text/html", ".js" => "application/javascript", ".css" => "text/css", ".png" => "image/png", _ => "application/octet-stream"
            };
            await route.FulfillAsync(new() { Status = 200, ContentType = contentType, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
}
