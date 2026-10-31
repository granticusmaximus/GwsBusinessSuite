using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Http;

namespace GwsBusinessSuite.Tests;

// A refused sign-in step has to say so (31-10-26): a bare 429 on the password form's /auth/login
// was re-run as the not-found page and bounced back to the sign-in page with no message, which
// read as "sign-in just refreshes" and sent Grant round in circles.
public sealed class SignInRateLimitsTests
{
    [Fact]
    public async Task ThePasswordForm_ShouldGoBackToTheSignInPage_SayingHowLongToWait()
    {
        var context = Request("POST", "/auth/login", accept: "text/html,application/xhtml+xml", fetchMode: "navigate");

        await SignInRateLimits.RejectAsync(context, TimeSpan.FromMinutes(11.2), CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be("/admin/login?error=ratelimit&minutes=12");
        context.Response.Headers.RetryAfter.ToString().Should().Be("672");
    }

    [Theory]
    [InlineData("POST", "/auth/mfa/verify")]
    [InlineData("GET", "/auth/touchid/web-login")]
    [InlineData("GET", "/auth/device-login/web")]
    public async Task OtherSignInSteps_LoadedAsAPage_ShouldAlsoGoBackToTheSignInPage(string method, string path)
    {
        var context = Request(method, path, accept: "text/html,*/*;q=0.8", fetchMode: null);

        await SignInRateLimits.RejectAsync(context, null, CancellationToken.None);

        context.Response.Headers.Location.ToString().Should().Be("/admin/login?error=ratelimit");
    }

    [Theory]
    [InlineData("/auth/passkey/login")]
    [InlineData("/auth/device-login")]
    [InlineData("/admin/api/native/lock-unlock-pin")]
    public async Task AScriptOrTheMacApp_ShouldGetA429WithAMessage(string path)
    {
        var context = Request("POST", path, accept: "*/*", fetchMode: "cors");

        await SignInRateLimits.RejectAsync(context, TimeSpan.FromMinutes(3), CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.ContentType.Should().StartWith("application/json", "a body keeps the status-code page from re-running it");
        Body(context).GetProperty("error").GetString().Should().Be("Too many sign-in attempts - try again in 3 minute(s).");
    }

    [Fact]
    public async Task TheMacAppsOtherCalls_ShouldGetAPlainMessage()
    {
        var context = Request("POST", "/admin/api/native/lock-device", accept: null, fetchMode: null);

        await SignInRateLimits.RejectAsync(context, null, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        Body(context).GetProperty("error").GetString().Should().Contain("Too many requests");
    }

    [Fact]
    public async Task EverythingElse_ShouldStayAPlain429()
    {
        var context = Request("GET", "/og-image", accept: "image/*", fetchMode: "no-cors");

        await SignInRateLimits.RejectAsync(context, null, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.ContentType.Should().BeNull("the /api/v1 problem+json page and others still apply");
        context.Response.Body.Length.Should().Be(0);
    }

    private static DefaultHttpContext Request(string method, string path, string? accept, string? fetchMode)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (accept is not null) context.Request.Headers.Accept = accept;
        if (fetchMode is not null) context.Request.Headers["Sec-Fetch-Mode"] = fetchMode;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonElement Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement.Clone();
    }
}
