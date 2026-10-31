using System.Security.Cryptography;
using System.Text.Json;
#if MACCATALYST
using Foundation;
using LocalAuthentication;
using Security;
#endif

namespace GwsBusinessSuite.App;

// Page lock phase 3 + Touch ID sign-in: this Mac's P-256 key for Sentinel. Tries the Secure
// Enclave first (the private key can't leave the chip; every signature needs Touch ID). This
// ad-hoc-signed build may not be allowed to create one (keychain entitlements break its launch -
// see the Aug 2026 SecureStorage note), so the fallback is a key file in the app container that the
// app only uses after LocalAuthentication confirms Touch ID. The server sees only the public key and
// verifies signatures over its own one-time challenges, so either storage works the same way.
//
// Setting Touch ID up again makes the new key beside the current one and only switches over once the
// server has accepted it (31-10-26): it used to overwrite the working key first and then delete it
// whenever the new one was refused or cancelled, which left the Mac with no key at all. KeyName
// tells the keys apart; null is a key from before then, under the original names.
public sealed record TouchIdKeyInfo(Guid ServerKeyId, string Storage, string? Username, string? KeyName = null);

public sealed class TouchIdKeyStore(string folder)
{
    private const string Tag = "net.gwsapp.sentinel.touchid.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string InfoPath => Path.Combine(folder, "touchid-key.json");

    private string KeyFilePath(TouchIdKeyInfo key) => Path.Combine(folder, key.KeyName is { } name ? $"touchid-key-{name}.pkcs8" : "touchid-key.pkcs8");

    private static string KeyTag(TouchIdKeyInfo key) => key.KeyName is { } name ? $"{Tag}.{name}" : Tag;

    public TouchIdKeyInfo? Info
    {
        get
        {
            try { return File.Exists(InfoPath) ? JsonSerializer.Deserialize<TouchIdKeyInfo>(File.ReadAllText(InfoPath), Json) : null; }
            catch (JsonException) { return null; }
        }
    }

    public void SaveInfo(TouchIdKeyInfo info)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(InfoPath, JsonSerializer.Serialize(info, Json));
    }

    public void Forget()
    {
        if (Info is { } current) Delete(current);
        if (File.Exists(InfoPath)) File.Delete(InfoPath);
    }

    // A new key beside the current one, which stays in use until Commit; its public key
    // (SubjectPublicKeyInfo) is what the server registers.
    public (TouchIdKeyInfo Key, byte[] PublicKeySpki) CreateKey()
    {
        var key = new TouchIdKeyInfo(Guid.Empty, "file", null, Guid.NewGuid().ToString("N"));
#if MACCATALYST
        if (TryCreateSecureEnclaveKey(KeyTag(key), out var spki, out _)) return (key with { Storage = "secure-enclave" }, spki);
#endif
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(KeyFilePath(key), ecdsa.ExportPkcs8PrivateKey());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFilePath(key), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return (key, ecdsa.ExportSubjectPublicKeyInfo());
    }

    // The new key, registered, becomes this Mac's key; the one before it goes.
    public void Commit(TouchIdKeyInfo key)
    {
        var previous = Info;
        SaveInfo(key);
        if (previous is not null && previous.KeyName != key.KeyName) Delete(previous);
    }

    // A new key that wasn't registered (cancelled, or refused): the current key stays as it was.
    public void Discard(TouchIdKeyInfo key)
    {
        if (Info?.KeyName != key.KeyName) Delete(key);
    }

    private void Delete(TouchIdKeyInfo key)
    {
        if (File.Exists(KeyFilePath(key))) File.Delete(KeyFilePath(key));
#if MACCATALYST
        SecKeyChain.Remove(new SecRecord(SecKind.Key) { ApplicationTag = NSData.FromString(KeyTag(key)) });
#endif
    }

    public Task<(byte[]? Signature, string? Error)> SignAsync(byte[] challenge, string reason) =>
        Info is { } info ? SignAsync(info, challenge, reason) : Task.FromResult<(byte[]?, string?)>((null, "Touch ID isn't set up on this Mac yet."));

    // Signs the challenge with the key after Touch ID (DER ECDSA over SHA-256). Null with a message
    // when the person cancels or Touch ID isn't available.
    public async Task<(byte[]? Signature, string? Error)> SignAsync(TouchIdKeyInfo info, byte[] challenge, string reason)
    {
#if MACCATALYST
        if (info.Storage == "secure-enclave")
        {
            // The key's access control asks for Touch ID on use; the context carries the prompt text
            // (the replacement for the obsolete UseOperationPrompt) and must outlive the signing.
            using var promptContext = new LAContext { LocalizedReason = reason };
            var key = SecKeyChain.QueryAsConcreteType(new SecRecord(SecKind.Key)
            {
                ApplicationTag = NSData.FromString(KeyTag(info)),
                KeyType = SecKeyType.ECSecPrimeRandom,
                AuthenticationContext = promptContext
            }, out var status) as SecKey;
            if (key is null) return (null, $"This Mac's Touch ID key is missing ({status}) - set Touch ID up again.");
            var signature = key.CreateSignature(SecKeyAlgorithm.EcdsaSignatureMessageX962Sha256, NSData.FromArray(challenge), out var error);
            return signature is null ? (null, error?.LocalizedDescription ?? "Touch ID was cancelled.") : (signature.ToArray(), null);
        }
        using var context = new LAContext();
        var (ok, laError) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, reason);
        if (!ok) return (null, laError?.LocalizedDescription ?? "Touch ID was cancelled.");
#else
        await Task.CompletedTask;
        return (null, "Touch ID needs the Mac app.");
#endif
#if MACCATALYST
        if (!File.Exists(KeyFilePath(info))) return (null, "This Mac's Touch ID key is missing - set Touch ID up again.");
        using var fileKey = ECDsa.Create();
        fileKey.ImportPkcs8PrivateKey(File.ReadAllBytes(KeyFilePath(info)), out _);
        return (fileKey.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), null);
#endif
    }

#if MACCATALYST
    // Secure Enclave key with Touch ID required for every use. Returns false (with the reason)
    // when this build isn't allowed to create one.
    public static bool TryCreateSecureEnclaveKey(string tag, out byte[] publicKeySpki, out string? failure)
    {
        publicKeySpki = [];
        failure = null;
        try
        {
            using var access = new SecAccessControl(SecAccessible.WhenUnlockedThisDeviceOnly,
                SecAccessControlCreateFlags.BiometryCurrentSet | SecAccessControlCreateFlags.PrivateKeyUsage);
            var parameters = new SecKeyGenerationParameters
            {
                KeyType = SecKeyType.ECSecPrimeRandom,
                KeySizeInBits = 256,
                TokenID = SecTokenID.SecureEnclave,
                PrivateKeyAttrs = new SecKeyParameters
                {
                    IsPermanent = true,
                    ApplicationTag = NSData.FromString(tag),
                    AccessControl = access
                }
            };
            using var key = SecKey.CreateRandomKey(parameters, out var error);
            if (key is null)
            {
                failure = error?.ToString() ?? "unknown error";
                return false;
            }
            using var publicKey = key.GetPublicKey();
            var x963 = publicKey?.GetExternalRepresentation()?.ToArray();
            if (x963 is not { Length: 65 })
            {
                failure = "no public key";
                return false;
            }
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x963[1..33], Y = x963[33..65] }
            });
            publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }
#endif
}
