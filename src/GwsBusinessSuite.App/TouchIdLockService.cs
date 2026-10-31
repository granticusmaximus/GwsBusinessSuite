namespace GwsBusinessSuite.App;

// Page lock phase 3 + Touch ID sign-in: the app side. The web page asks for these through
// gws-native:// links (MainPage.OnWorkspaceNavigating); the app does the Touch ID step itself
// and the server checks every signature.
public sealed class TouchIdLockService(NativeSessionClient session, TouchIdKeyStore store)
{
    public bool IsSetUp => store.Info is not null;

    // Creates a new key for this Mac and registers it. Touch ID is asked once first, so a Mac without
    // working Touch ID doesn't get a key it can't use. Until the server accepts the new key the
    // current one stays in use, so a cancel or a refusal never leaves the Mac without one.
    public async Task<(bool Ok, string Message)> EnrollAsync()
    {
        var (key, publicKey) = store.CreateKey();
        var (signature, error) = await store.SignAsync(key, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), "Set up Touch ID for Sentinel");
        if (signature is null)
        {
            store.Discard(key);
            return (false, error ?? "Touch ID didn't work.");
        }
        var (keyId, username, registerError) = await session.RegisterTouchIdKeyAsync(Environment.MachineName, publicKey, key.Storage);
        if (keyId is null)
        {
            store.Discard(key);
            return (false, registerError ?? "The server didn't accept this Mac's key.");
        }
        store.Commit(key with { ServerKeyId = keyId.Value, Username = username });
        var storage = key.Storage;
        return (true, storage == "secure-enclave"
            ? "Touch ID is set up on this Mac (key in the Secure Enclave)."
            : "Touch ID is set up on this Mac.");
    }

    public async Task<(bool Ok, string Message)> UnlockAsync()
    {
        if (store.Info is not { ServerKeyId: var keyId } || keyId == Guid.Empty) return (false, "Set up Touch ID for this Mac first (Page lock settings).");
        var (challenge, error) = await session.GetLockChallengeAsync(keyId);
        if (challenge is null) return (false, error ?? "Couldn't reach Sentinel.");
        var (signature, signError) = await store.SignAsync(Convert.FromBase64String(challenge), "Unlock your locked Sentinel pages");
        if (signature is null) return (false, signError ?? "Touch ID was cancelled.");
        var unlockError = await session.UnlockWithTouchIdAsync(keyId, signature);
        return unlockError is null ? (true, "Unlocked.") : (false, unlockError);
    }
}
