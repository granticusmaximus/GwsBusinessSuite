namespace GwsBusinessSuite.App;

internal enum SentinelWindowDestination
{
    Allow,
    Workspace,
    External
}

internal static class SentinelWindowNavigation
{
    public const string WindowPath = "/admin/sentinel/window";

    // Saved with a Sentinel window's scene so macOS restores it as Sentinel after a relaunch.
    public const string KindKey = "gws.window";
    public const string SentinelKind = "sentinel";
    public const string QueryKey = "gws.sentinel.query";

    // A scene macOS creates on its own (a restored window, or one more after the main window) is a
    // Sentinel window unless it is the only one: the app never has two main windows, which would
    // share the one set of toolbar and menu callbacks (19-11-26).
    public static bool IsSentinelScene(IReadOnlyDictionary<string, string?>? state, bool mainWindowOpen) =>
        mainWindowOpen
        || (state is not null && state.TryGetValue(KindKey, out var kind) && kind == SentinelKind);

    // The Sentinel window's address, keeping only the page or database to open from the request
    // (gws-native://sentinel-window?page=...), so the page can't steer the window anywhere else.
    public static string WindowUrl(string baseUrl, string? query)
    {
        var url = baseUrl.TrimEnd('/') + WindowPath;
        if (string.IsNullOrEmpty(query)) return url;
        var kept = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] is "page" or "database" or "row" && Guid.TryParse(pair[1], out _))
            .Select(pair => $"{pair[0]}={Guid.Parse(pair[1])}")
            .ToList();
        return kept.Count == 0 ? url : $"{url}?{string.Join('&', kept)}";
    }

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
