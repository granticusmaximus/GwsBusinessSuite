namespace GwsBusinessSuite.Application.Wiki;

// Page lock phase 3: Touch ID in the Mac app. Its web view can't use passkeys (WKWebView needs an
// Apple-provisioned entitlement), so the app holds its own P-256 key: in the Secure Enclave when
// this build can create one there, otherwise in a key file the app only uses after Touch ID. The
// server only ever sees the public key, and unlocks after verifying a signature over a fresh
// one-time challenge - the same protocol for both, so moving to the Secure Enclave later changes
// nothing here.
public static class SentinelDeviceKeys
{
    public const string SecureEnclave = "secure-enclave";
    public const string File = "file";
    public const int MaxKeysPerUser = 10;
}

public sealed record SentinelDeviceKeyView(Guid Id, string Name, string Storage, long CreatedAtUnix, long? LastUsedAtUnix);

public interface ISentinelDeviceKeyService
{
    // Adding a key when another unlock method exists needs a live unlock in this session.
    Task<SentinelDeviceKeyView> RegisterAsync(string username, string? sessionId, string name, string publicKeySpkiBase64, string storage, CancellationToken cancellationToken = default);
    // A one-time challenge (base64) for that key, valid for two minutes.
    Task<string> CreateChallengeAsync(string username, string? sessionId, Guid keyId, CancellationToken cancellationToken = default);
    // Verifies the ECDSA P-256 / SHA-256 signature (DER) over the challenge and unlocks this
    // person's pages in this session; returns the unlock's expiry (Unix seconds).
    Task<long> UnlockAsync(string username, string? sessionId, Guid keyId, string signatureBase64, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelDeviceKeyView>> ListAsync(string username, CancellationToken cancellationToken = default);
    // Touch ID sign-in to the Mac app (no session yet): a one-time challenge for the key, and the
    // key's owner when the signature over it checks out (null otherwise). An unknown key still gets
    // a challenge, so the endpoint doesn't reveal which keys exist.
    Task<string> CreateSignInChallengeAsync(Guid keyId, CancellationToken cancellationToken = default);
    Task<string?> CompleteSignInAsync(Guid keyId, string signatureBase64, CancellationToken cancellationToken = default);
    // Needs this session unlocked (RequireCanRemoveMethodAsync).
    Task RevokeAsync(Guid keyId, string username, string? sessionId, CancellationToken cancellationToken = default);
}
