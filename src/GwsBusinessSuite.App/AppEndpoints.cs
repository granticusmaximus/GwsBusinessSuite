namespace GwsBusinessSuite.App;

public static class AppEndpoints
{
    public const string ProductionBaseUrl = "https://admin.gwsapp.net";
    public const string AdminPortalPath = "/admin";

    public static string BaseUrl
    {
        get
        {
            var configured = Preferences.Default.Get(nameof(BaseUrl), ProductionBaseUrl).TrimEnd('/');
            return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? configured
                : ProductionBaseUrl;
        }
    }

    public static string StartUrl => $"{BaseUrl}{AdminPortalPath}";

    public static bool IsTrusted(Uri uri)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var configured)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == configured.Port;
    }
}
