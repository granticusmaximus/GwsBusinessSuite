using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// The sign-in page's fingerprint panel (31-10-26): what each browser is offered, and "Approve on my
// Mac" from asking to signing in. The server is stubbed: start hands out a number, the status
// answers whatever the test sets, and the panel follows it.
[Collection("Playwright")]
public sealed class TouchIdLoginBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly string Scripts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot/js"));
    private string _state = "waiting";
    private readonly List<string> _started = [];

    [Fact]
    public async Task ABrowserWithoutTouchId_ShouldOfferTheMac_AndAskWhoIsSigningIn()
    {
        await using var page = await OpenAsync();

        (await page.IsVisibleAsync("#gws-touchid")).Should().BeTrue();
        (await page.IsVisibleAsync("#gws-touchid-mac")).Should().BeTrue();
        (await page.IsVisibleAsync("#gws-touchid-here")).Should().BeFalse("this browser has no Touch ID");
        (await page.IsVisibleAsync("#gws-touchid-app")).Should().BeFalse("that's the Mac app's own button");

        await page.ClickAsync("#gws-touchid-mac");

        (await StateAsync(page)).Should().Be("error");
        (await page.InnerTextAsync("#gws-touchid-status")).Should().Contain("username");
        _started.Should().BeEmpty("nothing is asked of the server without a username");
    }

    [Fact]
    public async Task ApproveOnMyMac_ShouldShowTheNumber_ThenSignInOnceTheMacApproves()
    {
        await using var page = await OpenAsync();
        await page.FillAsync("#gws-login-username", "grant");

        await page.ClickAsync("#gws-touchid-mac");
        await page.WaitForFunctionAsync("() => document.getElementById('gws-touchid').dataset.state === 'waiting'");

        _started.Should().Equal("{\"username\":\"grant\"}");
        (await page.InnerTextAsync("#gws-touchid-number")).Should().Be("47");
        (await page.InnerTextAsync("#gws-touchid-status")).Should().Contain("pick 47");
        (await page.IsVisibleAsync("#gws-touchid-cancel")).Should().BeTrue();

        _state = "approved";
        var completing = page.WaitForURLAsync("**/auth/mac-approval/**/complete**");
        await page.Clock.RunForAsync(2100);
        await completing;
        page.Url.Should().Contain("secret=s3cret").And.Contain("returnUrl=%2Fadmin%2Fcrm");
    }

    [Fact]
    public async Task ADeclinedOrExpiredRequest_ShouldSaySo()
    {
        await using var page = await OpenAsync();
        await page.FillAsync("#gws-login-username", "grant");
        await page.ClickAsync("#gws-touchid-mac");
        await page.WaitForFunctionAsync("() => document.getElementById('gws-touchid').dataset.state === 'waiting'");

        _state = "declined";
        await page.Clock.RunForAsync(2100);
        await page.WaitForFunctionAsync("() => document.getElementById('gws-touchid').dataset.state === 'error'");
        (await page.InnerTextAsync("#gws-touchid-title")).Should().Be("Not approved");

        _state = "waiting";
        await page.ClickAsync("#gws-touchid-mac");
        await page.WaitForFunctionAsync("() => document.getElementById('gws-touchid').dataset.state === 'waiting'");
        await page.Clock.RunForAsync(125_000);
        await page.WaitForFunctionAsync("() => document.getElementById('gws-touchid').dataset.state === 'error'");
        (await page.InnerTextAsync("#gws-touchid-title")).Should().Be("No answer from your Mac");
    }

    private static Task<string> StateAsync(IPage page) => page.EvaluateAsync<string>("() => document.getElementById('gws-touchid').dataset.state");

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.Clock.InstallAsync();
        // Headless Chromium has no platform authenticator; say so plainly either way.
        await page.AddInitScriptAsync("() => { window.PublicKeyCredential = { isUserVerifyingPlatformAuthenticatorAvailable: async () => false }; }");
        await page.RouteAsync("http://localhost/admin/login", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """
                <!DOCTYPE html><html><head><base href="/" /></head><body>
                <section class="gws-touchid" id="gws-touchid" data-state="idle" data-return-url="/admin/crm" hidden>
                  <div class="gws-touchid-title" id="gws-touchid-title"></div>
                  <div class="gws-touchid-number" id="gws-touchid-number" hidden></div>
                  <div class="gws-touchid-status" id="gws-touchid-status"></div>
                  <button type="button" id="gws-touchid-here" hidden>Touch ID</button>
                  <button type="button" id="gws-touchid-mac" hidden>Approve on my Mac</button>
                  <a id="gws-touchid-app" href="gws-native://signin" hidden>Sign in with Touch ID</a>
                  <button type="button" id="gws-touchid-cancel" hidden>Cancel</button>
                </section>
                <div id="gws-touchid-divider" hidden></div>
                <form method="post" action="auth/login"><input id="gws-login-username" name="username" /></form>
                <script type="module" src="js/sentinel-passkey-login.js?v=2"></script>
                </body></html>
                """
        }));
        foreach (var script in new[] { "sentinel-passkey-login.js", "sentinel-passkey.js" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(Scripts, script));
            await page.RouteAsync($"http://localhost/js/{script}*", route => route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = source }));
        }
        await page.RouteAsync("http://localhost/auth/mac-approval/start", route =>
        {
            _started.Add(route.Request.PostData ?? string.Empty);
            return route.FulfillAsync(new() { Status = 200, ContentType = "application/json",
                Body = """{"id":"6f1c0d64-8f4e-4d0a-9a51-2b7f3c7c9e10","secret":"s3cret","number":47,"expiresInSeconds":120}""" });
        });
        await page.RouteAsync("http://localhost/auth/mac-approval/6f1c0d64-8f4e-4d0a-9a51-2b7f3c7c9e10?*", route =>
            route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"state":"{{_state}}"}""" }));
        await page.RouteAsync("http://localhost/auth/mac-approval/*/complete*", route =>
            route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<!DOCTYPE html><p>signed in</p>" }));
        await page.GotoAsync("http://localhost/admin/login");
        await page.WaitForFunctionAsync("() => !document.getElementById('gws-touchid').hidden");
        return page;
    }
}
