namespace GwsBusinessSuite.App;

// A developer-API key is a credential, not a preference - Keychain-backed SecureStorage, not
// the plist-backed Preferences the rest of the app uses for non-secret settings.
public sealed class SecureApiKeyStore
{
    private const string StorageKey = "sentinelgpt.sentinel-read-api-key";

    public Task<string?> GetAsync() => SecureStorage.Default.GetAsync(StorageKey);

    public Task SetAsync(string apiKey) => SecureStorage.Default.SetAsync(StorageKey, apiKey);

    public void Remove() => SecureStorage.Default.Remove(StorageKey);
}
