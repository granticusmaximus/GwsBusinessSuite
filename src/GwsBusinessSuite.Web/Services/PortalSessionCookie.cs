using Microsoft.AspNetCore.Authentication;

namespace GwsBusinessSuite.Web.Services;

// Which admin sign-ins survive the app or browser closing (05-11-26). Only the Mac app's own Touch ID
// sign-in ("device-key") is persistent. Quitting and reopening the Mac app within the session's fixed
// 8 hours keeps you signed in, instead of asking for Touch ID on every launch. The lifetime is
// unchanged: 8 hours from sign-in, never renewed (the cookie's ExpireTimeSpan, no sliding). Every
// other sign-in - password + MFA, passkey, a browser approved from the Mac, the device secret -
// stays a browser-session cookie, so closing that browser still signs it out.
public static class PortalSessionCookie
{
    public const string MacTouchIdAmr = "device-key";

    public static AuthenticationProperties PropertiesFor(string amr) =>
        new() { IsPersistent = string.Equals(amr, MacTouchIdAmr, StringComparison.Ordinal) };
}
