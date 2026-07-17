using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.LiveShow;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.Services;

public sealed class LiveShowIceServerProvider(
    IOptions<LiveShowIceOptions> options,
    TimeProvider timeProvider) : ILiveShowIceServerProvider
{
    public bool IsTurnConfigured
    {
        get
        {
            var configured = options.Value;
            var sharedSecret = configured.Turn.SharedSecret?.Trim() ?? string.Empty;
            return NormalizeUrls(configured.Turn.Urls, "turn:", "turns:").Count > 0
                && sharedSecret.Length >= 32;
        }
    }

    public LiveShowIceConfiguration CreateConfiguration()
    {
        var configured = options.Value;
        var iceServers = new List<LiveShowIceServerView>();

        var stunUrls = NormalizeUrls(configured.StunUrls, "stun:", "stuns:");
        if (stunUrls.Count > 0)
        {
            iceServers.Add(new LiveShowIceServerView(stunUrls));
        }

        var turnUrls = NormalizeUrls(configured.Turn.Urls, "turn:", "turns:");
        var sharedSecret = configured.Turn.SharedSecret?.Trim() ?? string.Empty;
        if (turnUrls.Count == 0 || sharedSecret.Length < 32)
        {
            return new LiveShowIceConfiguration(iceServers, false, null);
        }

        var lifetimeMinutes = Math.Clamp(configured.Turn.CredentialLifetimeMinutes, 5, 24 * 60);
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(lifetimeMinutes);
        var username = $"{expiresAt.ToUnixTimeSeconds()}:gws-live-{Guid.NewGuid():N}";
        var credential = CreateCredential(sharedSecret, username);

        iceServers.Add(new LiveShowIceServerView(turnUrls, username, credential));
        return new LiveShowIceConfiguration(iceServers, true, expiresAt);
    }

    internal static string CreateCredential(string sharedSecret, string username)
    {
        var key = Encoding.UTF8.GetBytes(sharedSecret);
        var payload = Encoding.UTF8.GetBytes(username);
        return Convert.ToBase64String(HMACSHA1.HashData(key, payload));
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string>? urls, params string[] allowedPrefixes) =>
        (urls ?? [])
            .Select(url => url.Trim())
            .Where(url => url.Length > 0 && allowedPrefixes.Any(prefix =>
                url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
