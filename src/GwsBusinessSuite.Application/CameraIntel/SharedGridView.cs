using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.Application.CameraIntel;

// A URL-encodable snapshot of the current globe view (position + open camera(s)), so a teammate
// can be sent directly to that exact view. Deliberately stateless - no DB row, no token/expiry,
// unlike Sentinel's heavier SentinelPublicShare system: a shared link only ever points back at
// /admin/osint, which stays behind the existing AdminOnly policy, so the recipient must already
// be an authenticated admin.
public sealed record SharedGridView(double Latitude, double Longitude, double HeightMeters, IReadOnlyList<CameraFeed> OpenCameras);

public static class SharedGridViewCodec
{
    // A sane ceiling matching the watch wall's own practical grid size - also guards TryDecode
    // against a maliciously or accidentally huge query string.
    private const int MaxOpenCameras = 9;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(SharedGridView view)
    {
        var json = JsonSerializer.Serialize(view, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        // Base64Url (RFC 4648 §5) rather than plain Base64 - a raw '+'/'/' would need further
        // percent-encoding to survive as a query string value, and '=' padding is dropped since
        // TryDecode below restores it from the string's own length.
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // Never throws - this decodes untrusted, hand-editable URL input (a person can paste a
    // mangled link, or edit the query string directly). Any failure - bad Base64, invalid JSON,
    // an out-of-range coordinate, too many cameras - falls back to null so the caller can show
    // the default view instead of crashing the page.
    public static SharedGridView? TryDecode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            var paddingNeeded = (4 - base64.Length % 4) % 4;
            base64 += new string('=', paddingNeeded);

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var view = JsonSerializer.Deserialize<SharedGridView>(json, JsonOptions);
            if (view is null) return null;
            if (view.Latitude is < -90 or > 90) return null;
            if (view.Longitude is < -180 or > 180) return null;
            if (view.HeightMeters <= 0) return null;
            if (view.OpenCameras is null || view.OpenCameras.Count > MaxOpenCameras) return null;

            return view;
        }
        catch
        {
            return null;
        }
    }
}
