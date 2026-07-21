namespace GwsBusinessSuite.Web.Services;

public static class PortalNavigation
{
    public const string DashboardPath = "/admin";

    public static string ResolvePostLoginPath(string? returnUrl) =>
        IsSafeLocalPath(returnUrl) ? returnUrl! : DashboardPath;

    private static bool IsSafeLocalPath(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith("/", StringComparison.Ordinal)
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.Contains("\\", StringComparison.Ordinal);
}
