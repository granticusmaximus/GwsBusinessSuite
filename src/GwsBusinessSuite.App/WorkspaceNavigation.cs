namespace GwsBusinessSuite.App;

// Lets another tab ask the Workspace tab to open a page of the hosted app (e.g. a note just saved
// to Sentinel). MainPage takes it the next time it appears; only trusted app URLs are honoured.
public static class WorkspaceNavigation
{
    private static string? _pendingUrl;

    public static void Request(string absoluteUrl) => _pendingUrl = absoluteUrl;

    public static string? TakePending()
    {
        var url = _pendingUrl;
        _pendingUrl = null;
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && AppEndpoints.IsTrusted(uri) ? url : null;
    }
}
