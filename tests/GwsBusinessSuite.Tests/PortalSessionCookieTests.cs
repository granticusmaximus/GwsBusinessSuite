using FluentAssertions;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

// 05-11-26: the Mac app asked for Touch ID on every launch because its session cookie died with the
// app. Only that sign-in now outlives the app; every browser sign-in still ends with the browser.
public sealed class PortalSessionCookieTests
{
    [Fact]
    public void TheMacAppsTouchIdSignIn_ShouldOutliveTheApp() =>
        PortalSessionCookie.PropertiesFor("device-key").IsPersistent.Should().BeTrue();

    [Theory]
    [InlineData("mfa")]
    [InlineData("passkey")]
    [InlineData("mac-approval")]
    [InlineData("device-secret")]
    [InlineData("dev-bypass")]
    public void EveryOtherSignIn_ShouldEndWithTheBrowser(string amr) =>
        PortalSessionCookie.PropertiesFor(amr).IsPersistent.Should().BeFalse();

    [Fact]
    public void ThePersistentSession_ShouldKeepTheCookiesFixedLifetime() =>
        PortalSessionCookie.PropertiesFor("device-key").ExpiresUtc.Should().BeNull("the cookie's 8-hour ExpireTimeSpan sets it, unrenewed");
}
