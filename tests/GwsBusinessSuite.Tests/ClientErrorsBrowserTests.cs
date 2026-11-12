using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Logging plan, Phase 1 (12-11-26): js/client-errors.js sends admin-page errors to the server's
// log with the page's trace id, once per message and at most ten a page, and stays quiet on
// public pages. The endpoint is replaced by a recorder.
[Collection("Playwright")]
public sealed class ClientErrorsBrowserTests(PlaywrightBrowserFixture fixture)
{
    private const string Trace = "4bf92f3577b34da6a3ce929d0e0e4736";
    private static readonly string Script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/client-errors.js"));

    private async Task<(IPage Page, List<JsonElement> Reports)> OpenAsync(bool admin)
    {
        var reports = new List<JsonElement>();
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/api/diagnostics/client")
            {
                lock (reports) reports.Add(JsonDocument.Parse(route.Request.PostData!).RootElement.Clone());
                await route.FulfillAsync(new() { Status = 204 });
            }
            else if (path == "/client-errors.js")
            {
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = await File.ReadAllTextAsync(Script) });
            }
            else
            {
                await route.FulfillAsync(new()
                {
                    Status = 200, ContentType = "text/html",
                    Body = $"""<!doctype html><head><base href="/"><meta name="gws-trace" content="{Trace}"></head><body>{(admin ? "<div class=\"gws-layout\"></div>" : "")}<script src="/client-errors.js"></script></body>"""
                });
            }
        });
        await page.GotoAsync("http://localhost/admin/osint?token=secret");
        return (page, reports);
    }

    [Fact]
    public async Task AdminPage_ShouldReportEachErrorOnce_WithItsTrace_AndCapThePage()
    {
        var (page, reports) = await OpenAsync(admin: true);
        await using var _ = page;

        await page.EvaluateAsync("() => { setTimeout(() => { throw new Error('map is not defined'); }); setTimeout(() => { throw new Error('map is not defined'); }); }");
        await page.EvaluateAsync("() => { Promise.reject(new Error('fetch failed')); }");
        await page.EvaluateAsync("() => window.gwsClientLog.report({ origin: 'mac-app', kind: 'native', message: 'Overwatch map: click on a pin that opens nothing' })");
        await page.WaitForFunctionAsync("() => true");
        await WaitForAsync(reports, 3);

        lock (reports)
        {
            reports.Select(report => report.GetProperty("message").GetString()).Should().BeEquivalentTo(
                ["Uncaught Error: map is not defined", "fetch failed", "Overwatch map: click on a pin that opens nothing"]);
            reports.Should().OnlyContain(report => report.GetProperty("trace").GetString() == Trace && report.GetProperty("page").GetString() == "/admin/osint");
            reports.Single(report => report.GetProperty("origin").GetString() == "mac-app").GetProperty("kind").GetString().Should().Be("native");
            reports.Single(report => report.GetProperty("kind").GetString() == "rejection").GetProperty("message").GetString().Should().Be("fetch failed");
        }

        await page.EvaluateAsync("() => { for (let i = 0; i < 20; i++) window.gwsClientLog.report({ message: 'distinct ' + i }); }");
        await WaitForAsync(reports, 10);
        await page.WaitForTimeoutAsync(300);
        lock (reports) reports.Should().HaveCount(10, "ten a page, so a loop can't flood the log");
    }

    [Fact]
    public async Task PublicPage_ShouldReportNothing()
    {
        var (page, reports) = await OpenAsync(admin: false);
        await using var _ = page;
        await page.EvaluateAsync("() => { setTimeout(() => { throw new Error('public error'); }); }");
        await page.WaitForTimeoutAsync(400);
        lock (reports) reports.Should().BeEmpty();
    }

    private static async Task WaitForAsync(List<JsonElement> reports, int count)
    {
        for (var i = 0; i < 50; i++)
        {
            lock (reports) if (reports.Count >= count) return;
            await Task.Delay(100);
        }
    }
}
