using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Web.Security;

// The Mac app's device sign-in, carried into its web view. The app's HTTP client can't pass a
// session cookie to the web view (on macOS the client keeps Set-Cookie to itself), so a successful
// /auth/device-login hands back a one-time code instead, and the app opens
// /auth/device-login/web?code=... in the web view, which signs the web view in. A code works once,
// for one minute.
public sealed class NativeSignInHandoff(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public string Issue(string username)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        cache.Set(Key(code), username, Lifetime);
        return code;
    }

    // The username the code was issued for, or null (unknown, expired or already used).
    public string? Redeem(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 64) return null;
        var key = Key(code.ToLowerInvariant());
        if (!cache.TryGetValue(key, out string? username)) return null;
        cache.Remove(key);
        return username;
    }

    private static string Key(string code) => $"native-signin-handoff:{code}";
}
