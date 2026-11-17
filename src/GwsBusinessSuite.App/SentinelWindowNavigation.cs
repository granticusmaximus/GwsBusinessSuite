namespace GwsBusinessSuite.App;

internal enum SentinelWindowDestination
{
    Allow,
    Workspace,
    External
}

internal static class SentinelWindowNavigation
{
    public static SentinelWindowDestination Classify(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Equals("/admin", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/admin/sentinel", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/admin/wiki", StringComparison.OrdinalIgnoreCase))
            return SentinelWindowDestination.Workspace;

        if (path.Equals("/admin/sentinel/window", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/sentinel/present/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/admin/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/login/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
            return SentinelWindowDestination.Allow;

        return SentinelWindowDestination.External;
    }
}
