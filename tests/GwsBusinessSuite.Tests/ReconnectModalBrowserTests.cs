using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// The "Rejoining..." modal waits a short grace before showing, so a quick reconnect (coming back
// to a tab) doesn't flash it - but a failed reconnect must always show it, for its Retry button,
// and so must a paused session: until it's resumed the page ignores every click (19-10-26: the Mac
// app's Sentinel "does nothing at all" after a server restart, because "paused" was ignored).
[Collection("Playwright")]
public sealed class ReconnectModalBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string ScriptPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Layout/ReconnectModal.razor.js"));

    [Fact]
    public async Task QuickReconnect_ShouldNeverShowTheModal()
    {
        await using var page = await OpenAsync();
        await SetStateAsync(page, "show");
        await page.Clock.RunForAsync(1000);
        (await IsOpenAsync(page)).Should().BeFalse("a reconnect still inside the grace mustn't flash the modal");
        await SetStateAsync(page, "hide");
        await page.Clock.RunForAsync(5000);
        (await IsOpenAsync(page)).Should().BeFalse();
    }

    [Fact]
    public async Task SlowReconnect_ShouldShowTheModalAfterTheGrace_AndCloseOnHide()
    {
        await using var page = await OpenAsync();
        await SetStateAsync(page, "show");
        await page.Clock.RunForAsync(2000);
        (await IsOpenAsync(page)).Should().BeFalse();
        await page.Clock.RunForAsync(1000);
        (await IsOpenAsync(page)).Should().BeTrue();
        await SetStateAsync(page, "hide");
        (await IsOpenAsync(page)).Should().BeFalse();
    }

    [Fact]
    public async Task FailureInsideTheGrace_ShouldStillShowTheModal()
    {
        await using var page = await OpenAsync();
        await SetStateAsync(page, "show");
        await page.Clock.RunForAsync(500);
        await SetStateAsync(page, "failed");
        (await IsOpenAsync(page)).Should().BeTrue();
        await page.Clock.RunForAsync(5000);
        (await IsOpenAsync(page)).Should().BeTrue("the pending show must not reopen or close it");
    }

    [Fact]
    public async Task Pause_ShouldShowTheModalAtOnce_EvenRightAfterShow()
    {
        // Blazor sends "show" then "paused" back to back; the pause cancels the grace timer.
        await using var page = await OpenAsync();
        await SetStateAsync(page, "show");
        await SetStateAsync(page, "paused");
        (await IsOpenAsync(page)).Should().BeTrue("a paused page ignores clicks, so it can't stay silent");
        await page.Clock.RunForAsync(5000);
        (await IsOpenAsync(page)).Should().BeTrue();
    }

    [Fact]
    public async Task PausedPage_ShouldResumeByItself_OnceWhenShownAgain()
    {
        await using var page = await OpenAsync();
        await page.EvaluateAsync("() => { window.__resumes = 0; window.Blazor = { resumeCircuit: async () => { window.__resumes++; return true; } }; }");
        await SetStateAsync(page, "show");
        await SetStateAsync(page, "paused");
        await page.EvaluateAsync("() => document.dispatchEvent(new Event('visibilitychange'))");
        await page.EvaluateAsync("() => document.dispatchEvent(new Event('visibilitychange'))");
        (await page.EvaluateAsync<int>("() => window.__resumes")).Should().Be(1);
    }

    private static Task SetStateAsync(IPage page, string state) => page.EvaluateAsync(
        "state => document.getElementById('components-reconnect-modal').dispatchEvent(new CustomEvent('components-reconnect-state-changed', { detail: { state } }))",
        state);

    private static Task<bool> IsOpenAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.getElementById('components-reconnect-modal').open");

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.Clock.InstallAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """
                <!DOCTYPE html><html><body>
                <dialog id="components-reconnect-modal"><button id="components-reconnect-button">Retry</button>
                <button id="components-resume-button">Resume</button></dialog>
                </body></html>
                """
        }));
        var source = await File.ReadAllTextAsync(ScriptPath);
        await page.RouteAsync("http://localhost/reconnect.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = source
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/reconnect.js" });
        return page;
    }
}
