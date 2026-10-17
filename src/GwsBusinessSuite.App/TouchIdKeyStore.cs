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
public sealed record TouchIdKeyInfo(Guid ServerKeyId, string Storage, string? Username);

public sealed class TouchIdKeyStore(string folder)
{
    private const string Tag = "net.gwsapp.sentinel.touchid.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string InfoPath => Path.Combine(folder, "touchid-key.json");
    private string KeyFilePath => Path.Combine(folder, "touchid-key.pkcs8");

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
        foreach (var path in new[] { InfoPath, KeyFilePath }) if (File.Exists(path)) File.Delete(path);
#if MACCATALYST
        SecKeyChain.Remove(new SecRecord(SecKind.Key) { ApplicationTag = NSData.FromString(Tag) });
#endif
    }

    // Creates a new key (replacing any old one); returns its public key (SubjectPublicKeyInfo) and
    // where it lives.
    public (byte[] PublicKeySpki, string Storage) CreateKey()
    {
        Forget();
#if MACCATALYST
        if (TryCreateSecureEnclaveKey(out var spki, out _)) return (spki, "secure-enclave");
#endif
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(KeyFilePath, key.ExportPkcs8PrivateKey());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return (key.ExportSubjectPublicKeyInfo(), "file");
    }

    // Signs the challenge after Touch ID (DER ECDSA over SHA-256). Null with a message when the
    // person cancels or Touch ID isn't available.
    public async Task<(byte[]? Signature, string? Error)> SignAsync(byte[] challenge, string reason)
    {
        var info = Info;
        if (info is null) return (null, "Touch ID isn't set up on this Mac yet.");
#if MACCATALYST
        if (info.Storage == "secure-enclave")
        {
            // The key's access control asks for Touch ID on use.
            var key = SecKeyChain.QueryAsConcreteType(new SecRecord(SecKind.Key)
            {
                ApplicationTag = NSData.FromString(Tag),
                KeyType = SecKeyType.ECSecPrimeRandom,
                UseOperationPrompt = reason
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
        if (!File.Exists(KeyFilePath)) return (null, "This Mac's Touch ID key is missing - set Touch ID up again.");
        using var fileKey = ECDsa.Create();
        fileKey.ImportPkcs8PrivateKey(File.ReadAllBytes(KeyFilePath), out _);
        return (fileKey.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), null);
#endif
    }

#if MACCATALYST
    // Secure Enclave key with Touch ID required for every use. Returns false (with the reason)
    // when this build isn't allowed to create one.
    public static bool TryCreateSecureEnclaveKey(out byte[] publicKeySpki, out string? failure)
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
                    ApplicationTag = NSData.FromString(Tag),
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
