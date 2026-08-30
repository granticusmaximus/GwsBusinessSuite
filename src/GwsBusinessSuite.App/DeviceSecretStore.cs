namespace GwsBusinessSuite.App;

// The shared secret this app presents to /auth/device-login to skip the interactive MFA
// challenge against the real server (see IUserManagementService.AttemptDeviceLoginAsync) - a
// real, separate credential from the account password, not derived from anything a browser
// could send. Same file-based storage convention as SecureApiKeyStore (plain file inside this
// app's sandboxed container, owner-only Unix file mode) for the same reason documented there:
// Keychain writes fail under App Sandbox without a keychain-access-groups entitlement this
// ad-hoc-signed local build doesn't have.
public sealed class DeviceSecretStore(string filePath)
{
    public async Task<string?> GetAsync()
    {
        if (!File.Exists(filePath)) return null;
        var value = await File.ReadAllTextAsync(filePath);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public async Task SetAsync(string secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, secret);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Remove()
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }
}
