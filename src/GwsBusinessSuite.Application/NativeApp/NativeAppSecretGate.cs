using System.Security.Cryptography;
using System.Text;

namespace GwsBusinessSuite.Application.NativeApp;

// The one thing that proves an HTTP caller is the user's own trusted macOS app rather than an
// arbitrary browser request: a pre-provisioned shared secret (NativeApp:DeviceSecret), compared
// with a fixed-time equality check so a wrong guess can't be timed to learn how much of it
// matched. Shared by every native-app-only endpoint (device-login, the SentinelGPT fallback chat)
// so there's exactly one tested implementation of this comparison instead of one per endpoint.
public static class NativeAppSecretGate
{
    public static bool IsValid(string? providedSecret, string? configuredSecret)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret) || string.IsNullOrEmpty(providedSecret)
            || providedSecret.Length != configuredSecret.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSecret), Encoding.UTF8.GetBytes(configuredSecret));
    }
}
