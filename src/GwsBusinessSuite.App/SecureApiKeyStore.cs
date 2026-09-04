namespace GwsBusinessSuite.App;

// A developer-API key is a credential, not a preference - originally Keychain-backed
// SecureStorage, not the plist-backed Preferences the rest of the app uses for non-secret
// settings. Moved to a plain file inside this app's own sandboxed container (the same
// mechanism ConversationSessionStore/ApprovedMemoryStore already use) after confirming Keychain
// writes fail under App Sandbox without a keychain-access-groups entitlement, which in turn
// requires a real Apple provisioning profile this ad-hoc-signed local build doesn't have -
// adding it broke the app's ability to launch at all. See
// project_securestorage_keychain_entitlement_2026_08_25 (Claude session memory) for the full
// investigation. Real tradeoff, accepted deliberately: this app is single-user/local and the
// key itself is a scoped, read-only sentinel:read API key, not a master credential, so the
// sandbox container boundary plus owner-only file permissions are an acceptable substitute for
// genuine Keychain hardening here.
public sealed class SecureApiKeyStore(string filePath)
{
    public async Task<string?> GetAsync()
    {
        if (!File.Exists(filePath)) return null;
        var value = await File.ReadAllTextAsync(filePath);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Sync and cheap (no file read) so callers on a hot path - NativeToolExecutor.Definitions is
    // consulted once per tool-calling round - can decide whether a key exists without awaiting.
    public bool HasKey() => File.Exists(filePath) && new FileInfo(filePath).Length > 0;

    public async Task SetAsync(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, apiKey);
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
