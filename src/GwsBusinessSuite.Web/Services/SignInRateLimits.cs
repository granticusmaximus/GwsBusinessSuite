using System.Globalization;

namespace GwsBusinessSuite.Web.Services;

// What a rate-limited request gets back (31-10-26). Only the sign-in page itself used to get a
// message. The password form posts to /auth/login, so a refusal there was a bare 429, which the
// status-code page re-ran as POST /__not-found. No endpoint answers that, so the admin-only
// fallback sent it to the sign-in page with no word of why: Grant hit that loop after five sign-in
// steps in 15 minutes. Now a page load (the form, a link, the Mac app's web view) goes back to the
// sign-in page saying how long to wait, and a script or the Mac app gets a 429 with a JSON error.
public static class SignInRateLimits
{
    public static async ValueTask RejectAsync(HttpContext context, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        int? minutes = retryAfter is { } wait ? Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)) : null;
        if (retryAfter is { } seconds)
            response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(seconds.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        var signIn = IsSignInStep(request.Path);
        if (request.Path.StartsWithSegments("/admin/login") || (signIn && IsPageLoad(request)))
        {
            response.Redirect(minutes is { } m ? $"/admin/login?error=ratelimit&minutes={m}" : "/admin/login?error=ratelimit");
            return;
        }

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        // A body also keeps the status-code page from re-running the request (see above).
        if (signIn || request.Path.StartsWithSegments("/admin/api/native"))
        {
            var when = minutes is { } m ? $"{m} minute(s)" : "a few minutes";
            await response.WriteAsJsonAsync(new
            {
                error = signIn ? $"Too many sign-in attempts - try again in {when}." : "Too many requests - wait a moment, then try again."
            }, cancellationToken);
        }
    }

    private static bool IsSignInStep(PathString path) =>
        path.StartsWithSegments("/auth") || path.StartsWithSegments("/admin/api/native/lock-unlock-pin")
        || path.StartsWithSegments("/admin/api/native/lock-unlock-authenticator");

    // A navigation (a form post, a link, the Mac app's web view), not fetch() or an HTTP client.
    private static bool IsPageLoad(HttpRequest request) =>
        string.Equals(request.Headers["Sec-Fetch-Mode"], "navigate", StringComparison.OrdinalIgnoreCase)
        || request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
}
