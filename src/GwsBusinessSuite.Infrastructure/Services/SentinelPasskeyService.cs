using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace GwsBusinessSuite.Infrastructure.Services;

// Page lock phase 2 (SentinelPasskeys): WebAuthn via fido2-net-lib (MIT).
public sealed class SentinelPasskeyService(
    IAppDbContext db,
    ISentinelPageLockService pageLocks,
    IMemoryCache cache,
    IConfiguration configuration,
    TimeProvider timeProvider) : ISentinelPasskeyService
{
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);
    // WebAuthn's "client-device" hint: the browser offers this device's Touch ID first.
    private static readonly IReadOnlyList<PublicKeyCredentialHint> ThisDevice = [PublicKeyCredentialHint.ClientDevice];
    private const int MaxPasskeys = 10;

    public async Task<string> BeginRegistrationAsync(string username, string? sessionId, string origin, CancellationToken cancellationToken = default)
    {
        await pageLocks.RequireCanAddMethodAsync(username, sessionId, cancellationToken);
        var fido = Create(origin);
        var existing = await db.SentinelPasskeys.AsNoTracking().Where(key => key.Username == username).ToListAsync(cancellationToken);
        if (existing.Count >= MaxPasskeys) throw new SentinelLockException($"You already have {MaxPasskeys} passkeys - remove one first.");
        var user = new Fido2User { Id = UserHandle(username), Name = username, DisplayName = username };
        var options = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = existing.Select(key => new PublicKeyCredentialDescriptor(System.Buffers.Text.Base64Url.DecodeFromChars(key.CredentialId))).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // Touch ID / Face ID on this device (or a synced iCloud Keychain passkey), with the
                // fingerprint always asked for.
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        // Leads with Touch ID: without it Chrome opened on "use a phone (QR code) or a security
        // key", which read as if this Mac couldn't do it (31-10-26).
        options.Hints = ThisDevice;
        cache.Set(RegistrationKey(username), options.ToJson(), CeremonyLifetime);
        return options.ToJson();
    }

    public async Task<SentinelPasskeyView> CompleteRegistrationAsync(string username, string origin, string name, string attestationJson, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(RegistrationKey(username), out string? optionsJson) || optionsJson is null)
            throw new SentinelLockException("That took too long - start adding the passkey again.");
        cache.Remove(RegistrationKey(username));
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Touch ID" : name.Trim()[..Math.Min(name.Trim().Length, 60)];
        RegisteredPublicKeyCredential credential;
        try
        {
            var response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson)
                ?? throw new SentinelLockException("The browser sent no passkey.");
            credential = await Create(origin).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(optionsJson),
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                {
                    var id = System.Buffers.Text.Base64Url.EncodeToString(args.CredentialId);
                    return !await db.SentinelPasskeys.AnyAsync(key => key.CredentialId == id, ct);
                }
            }, cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            throw new SentinelLockException($"The passkey couldn't be verified: {ex.Message}");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var passkey = new SentinelPasskey
        {
            Username = username,
            Name = trimmedName,
            CredentialId = System.Buffers.Text.Base64Url.EncodeToString(credential.Id),
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            AaGuid = credential.AaGuid,
            CreatedAtUnix = now,
            CreatedBy = username
        };
        db.SentinelPasskeys.Add(passkey);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(passkey);
    }

    public async Task<string> BeginUnlockAsync(string username, string? sessionId, string origin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new SentinelLockException("Sign out and sign in again to use locked pages (this sign-in predates page locks).");
        var keys = await db.SentinelPasskeys.AsNoTracking().Where(key => key.Username == username).ToListAsync(cancellationToken);
        if (keys.Count == 0) throw new SentinelLockException("Add a Touch ID passkey first (Page lock settings).");
        var options = Create(origin).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = keys.Select(key => new PublicKeyCredentialDescriptor(System.Buffers.Text.Base64Url.DecodeFromChars(key.CredentialId))).ToList(),
            UserVerification = UserVerificationRequirement.Required
        });
        options.Hints = ThisDevice;
        cache.Set(UnlockKey(username, sessionId), options.ToJson(), CeremonyLifetime);
        return options.ToJson();
    }

    public async Task<long> CompleteUnlockAsync(string username, string? sessionId, string origin, string assertionJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId) || !cache.TryGetValue(UnlockKey(username, sessionId), out string? optionsJson) || optionsJson is null)
            throw new SentinelLockException("That took too long - try Touch ID again.");
        // One answer per challenge: a replayed response finds nothing to check against.
        cache.Remove(UnlockKey(username, sessionId));
        var response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson)
            ?? throw new SentinelLockException("The browser sent no Touch ID answer.");
        var credentialId = System.Buffers.Text.Base64Url.EncodeToString(response.RawId);
        var passkey = await db.SentinelPasskeys.FirstOrDefaultAsync(key => key.CredentialId == credentialId && key.Username == username, cancellationToken)
            ?? throw new SentinelLockException("That passkey isn't registered for you.");
        try
        {
            var result = await Create(origin).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(optionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle is null || args.UserHandle.AsSpan().SequenceEqual(UserHandle(username)))
            }, cancellationToken);
            passkey.SignCount = result.SignCount;
        }
        catch (Fido2VerificationException ex)
        {
            throw new SentinelLockException($"Touch ID couldn't be verified: {ex.Message}");
        }
        passkey.LastUsedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
        return await pageLocks.GrantAsync(username, sessionId, SentinelPageLock.Methods.Passkey, cancellationToken);
    }

    public Task<(string CeremonyId, string OptionsJson)> BeginSignInAsync(string origin, CancellationToken cancellationToken = default)
    {
        // No allowed-credentials list: the browser offers this site's passkeys on the device.
        var options = Create(origin).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required
        });
        options.Hints = ThisDevice;
        var ceremonyId = Guid.NewGuid().ToString("N");
        cache.Set(SignInKey(ceremonyId), options.ToJson(), CeremonyLifetime);
        return Task.FromResult((ceremonyId, options.ToJson()));
    }

    public async Task<string> CompleteSignInAsync(string ceremonyId, string origin, string assertionJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ceremonyId) || !cache.TryGetValue(SignInKey(ceremonyId), out string? optionsJson) || optionsJson is null)
            throw new SentinelLockException("That took too long - try again.");
        cache.Remove(SignInKey(ceremonyId));
        var response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson)
            ?? throw new SentinelLockException("The browser sent no Touch ID answer.");
        var credentialId = System.Buffers.Text.Base64Url.EncodeToString(response.RawId);
        var passkey = await db.SentinelPasskeys.FirstOrDefaultAsync(key => key.CredentialId == credentialId, cancellationToken)
            ?? throw new SentinelLockException("That passkey isn't registered here.");
        try
        {
            var result = await Create(origin).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(optionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                // A discoverable passkey names its owner (the user handle); it must be this one.
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle is not null && args.UserHandle.AsSpan().SequenceEqual(UserHandle(passkey.Username)))
            }, cancellationToken);
            passkey.SignCount = result.SignCount;
        }
        catch (Fido2VerificationException ex)
        {
            throw new SentinelLockException($"Touch ID couldn't be verified: {ex.Message}");
        }
        passkey.LastUsedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
        return passkey.Username;
    }

    private static string SignInKey(string ceremonyId) => $"sentinel-passkey-signin:{ceremonyId}";

    public async Task<IReadOnlyList<SentinelPasskeyView>> ListAsync(string username, CancellationToken cancellationToken = default) =>
        (await db.SentinelPasskeys.AsNoTracking().Where(key => key.Username == username).ToListAsync(cancellationToken))
            .OrderBy(key => key.CreatedAtUnix).Select(ToView).ToList();

    public async Task RemoveAsync(Guid passkeyId, string username, CancellationToken cancellationToken = default)
    {
        var passkey = await db.SentinelPasskeys.FirstOrDefaultAsync(key => key.Id == passkeyId && key.Username == username, cancellationToken);
        if (passkey is null) return;
        db.SentinelPasskeys.Remove(passkey);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The relying party is the page's own host; only Sentinel's own hosts are accepted. The admin
    // also runs on the public site's hosts (same app, for in-place editing), so those are allowed
    // too, and www shares the bare domain's relying party so one passkey works on both.
    private Fido2 Create(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) throw new SentinelLockException("Touch ID isn't available on this address.");
        var allowed = configuration.GetSection("Sentinel:Passkeys:Hosts").Get<string[]>()
            ?? ["admin.gwsapp.net", "grantwatson.dev", "www.grantwatson.dev"];
        var isLocal = uri.Host is "localhost" or "127.0.0.1";
        if (!(isLocal || (uri.Scheme == Uri.UriSchemeHttps && allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))))
            throw new SentinelLockException("Touch ID isn't available on this address.");
        var normalized = uri.GetLeftPart(UriPartial.Authority);
        var rpId = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && allowed.Contains(uri.Host[4..], StringComparer.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        return new Fido2(new Fido2Configuration
        {
            RPID = rpId,
            RPName = "GWS Sentinel",
            Origins = new HashSet<string>([normalized])
        });
    }

    // A stable, non-identifying handle per person (WebAuthn user.id).
    private static byte[] UserHandle(string username) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("gws-sentinel:" + username.ToLowerInvariant()))[..32];

    private static string RegistrationKey(string username) => $"sentinel-passkey-register:{username.ToLowerInvariant()}";
    private static string UnlockKey(string username, string sessionId) => $"sentinel-passkey-unlock:{username.ToLowerInvariant()}:{sessionId}";

    private static SentinelPasskeyView ToView(SentinelPasskey key) => new(key.Id, key.Name, key.CreatedAtUnix, key.LastUsedAtUnix);
}
