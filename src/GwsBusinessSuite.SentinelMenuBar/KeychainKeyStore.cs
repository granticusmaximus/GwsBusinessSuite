using Foundation;
using Security;

namespace GwsBusinessSuite.SentinelMenuBar;

// The Developer API key (sentinel:read) this companion reads status with, kept in the login
// Keychain - never in a file or user defaults.
internal static class KeychainKeyStore
{
    private const string Service = "net.gwsapp.sentinel-menubar";
    private const string Account = "developer-api-key";

    public static string? Read()
    {
        var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account };
        var match = SecKeyChain.QueryAsRecord(query, out var status);
        return status == SecStatusCode.Success && match?.ValueData is { } data ? data.ToString(NSStringEncoding.UTF8) : null;
    }

    public static void Save(string key)
    {
        Remove();
        SecKeyChain.Add(new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Account,
            Label = "GWS Sentinel menu bar API key",
            ValueData = NSData.FromString(key, NSStringEncoding.UTF8),
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly
        });
    }

    public static void Remove() =>
        SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account });
}
