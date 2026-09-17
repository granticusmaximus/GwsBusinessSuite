namespace GwsBusinessSuite.Web.Services;

public static class PortalNavigation
{
    public const string DashboardPath = "/admin";

    public static string ResolvePostLoginPath(string? returnUrl) =>
        IsSafeLocalPath(returnUrl) ? returnUrl! : DashboardPath;

    // /__not-found is UseStatusCodePagesWithReExecute's internal re-execution target (see
    // Program.cs) - a request that 404'd or was denied ends up re-executed against this path,
    // and its path can leak into a returnUrl a user never actually asked to go to (e.g. an
    // expired/invalid deep link that 404'd before the login challenge even fired). Treating it
    // as a "safe" post-login destination sends an otherwise-successful sign-in straight to
    // /admin/access-denied instead of anywhere useful - it's machinery, never a real destination.
    public static bool IsSafeLocalPath(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith("/", StringComparison.Ordinal)
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.Contains("\\", StringComparison.Ordinal)
        && !returnUrl.Equals("/__not-found", StringComparison.OrdinalIgnoreCase)
        && !returnUrl.StartsWith("/__not-found?", StringComparison.OrdinalIgnoreCase)
        && !returnUrl.StartsWith("/__not-found#", StringComparison.OrdinalIgnoreCase);
}
