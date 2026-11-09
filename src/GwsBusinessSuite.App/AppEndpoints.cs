namespace GwsBusinessSuite.App;

public static class AppEndpoints
{
    public const string DefaultAppHost = "https://gws-pooldp";
    public const string ProductionBaseUrl = "https://gws-pooldp";
    public const string AdminPortalPath = "/admin";

    private static string ResolveBaseUrl(string fallback)
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("GWS_APP_BASE_URL"),
                     Environment.GetEnvironmentVariable("GWS_BASE_URL"),
                     Environment.GetEnvironmentVariable("GWS_HOST_URL")
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var trimmed = candidate.TrimEnd('/');
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    return trimmed;
                }
            }
        }

        var configured = Preferences.Default.Get(nameof(BaseUrl), fallback).TrimEnd('/');
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri2) && uri2.Scheme == Uri.UriSchemeHttps
            ? configured
            : fallback;
    }

    public static string BaseUrl => ResolveBaseUrl(DefaultAppHost);

    public static string StartUrl => $"{BaseUrl}{AdminPortalPath}";

    public static bool IsTrusted(Uri uri)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var configured)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == configured.Port;
    }
}
