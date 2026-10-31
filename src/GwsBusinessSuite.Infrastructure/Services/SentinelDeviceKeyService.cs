using System.Security.Cryptography;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Infrastructure.Services;

// Page lock phase 3 (SentinelDeviceKeys): the Mac app's Touch ID key.
public sealed class SentinelDeviceKeyService(
    IAppDbContext db,
    ISentinelPageLockService pageLocks,
    IMemoryCache cache,
    TimeProvider timeProvider) : ISentinelDeviceKeyService
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);

    public async Task<SentinelDeviceKeyView> RegisterAsync(string username, string? sessionId, string name, string publicKeySpkiBase64, string storage, CancellationToken cancellationToken = default)
    {
        await pageLocks.RequireCanAddMethodAsync(username, sessionId, cancellationToken);
        if (storage is not (SentinelDeviceKeys.SecureEnclave or SentinelDeviceKeys.File)) throw new SentinelLockException("Unknown key storage.");
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKeySpkiBase64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            if (key.KeySize != 256) throw new SentinelLockException("The Mac app sent a key of the wrong size.");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new SentinelLockException("The Mac app sent a key Sentinel can't read.");
        }
        if (await db.SentinelDeviceKeys.CountAsync(key => key.Username == username && key.RevokedAtUnix == null, cancellationToken) >= SentinelDeviceKeys.MaxKeysPerUser)
            throw new SentinelLockException($"You already have {SentinelDeviceKeys.MaxKeysPerUser} Macs set up - remove one first.");
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Mac" : name.Trim()[..Math.Min(name.Trim().Length, 60)];
        var deviceKey = new SentinelDeviceKey
        {
            Username = username, Name = trimmed, PublicKeySpki = spki, Storage = storage,
            CreatedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds(), CreatedBy = username
        };
        db.SentinelDeviceKeys.Add(deviceKey);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(deviceKey);
    }

    public async Task<string> CreateChallengeAsync(string username, string? sessionId, Guid keyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new SentinelLockException("Sign out and sign in again to use locked pages (this sign-in predates page locks).");
        if (!await db.SentinelDeviceKeys.AnyAsync(key => key.Id == keyId && key.Username == username && key.RevokedAtUnix == null, cancellationToken))
            throw new SentinelLockException("This Mac's Touch ID key isn't set up for you any more - set it up again in Page lock settings.");
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        cache.Set(ChallengeKey(username, sessionId, keyId), challenge, ChallengeLifetime);
        return challenge;
    }

    public async Task<long> UnlockAsync(string username, string? sessionId, Guid keyId, string signatureBase64, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId) || !cache.TryGetValue(ChallengeKey(username, sessionId, keyId), out string? challenge) || challenge is null)
            throw new SentinelLockException("That took too long - try Touch ID again.");
        // One answer per challenge.
        cache.Remove(ChallengeKey(username, sessionId, keyId));
        var deviceKey = await db.SentinelDeviceKeys.FirstOrDefaultAsync(key => key.Id == keyId && key.Username == username && key.RevokedAtUnix == null, cancellationToken)
            ?? throw new SentinelLockException("This Mac's Touch ID key isn't set up for you any more.");
        if (!Verify(deviceKey, challenge, signatureBase64)) throw new SentinelLockException("Touch ID couldn't be verified.");
        deviceKey.LastUsedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
        return await pageLocks.GrantAsync(username, sessionId, SentinelPageLock.Methods.Device, cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelDeviceKeyView>> ListAsync(string username, CancellationToken cancellationToken = default) =>
        (await db.SentinelDeviceKeys.AsNoTracking().Where(key => key.Username == username && key.RevokedAtUnix == null).ToListAsync(cancellationToken))
            .OrderBy(key => key.CreatedAtUnix).Select(ToView).ToList();

    public async Task RevokeAsync(Guid keyId, string username, string? sessionId, CancellationToken cancellationToken = default)
    {
        var key = await db.SentinelDeviceKeys.FirstOrDefaultAsync(item => item.Id == keyId && item.Username == username, cancellationToken);
        if (key is null) return;
        await pageLocks.RequireCanRemoveMethodAsync(username, sessionId, cancellationToken);
        key.RevokedAtUnix ??= timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<string> CreateSignInChallengeAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        cache.Set(SignInKey(keyId), challenge, ChallengeLifetime);
        return Task.FromResult(challenge);
    }

    public async Task<string?> CompleteSignInAsync(Guid keyId, string signatureBase64, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(SignInKey(keyId), out string? challenge) || challenge is null) return null;
        cache.Remove(SignInKey(keyId));
        var deviceKey = await db.SentinelDeviceKeys.FirstOrDefaultAsync(key => key.Id == keyId && key.RevokedAtUnix == null, cancellationToken);
        if (deviceKey is null || !Verify(deviceKey, challenge, signatureBase64)) return null;
        deviceKey.LastUsedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
        return deviceKey.Username;
    }

    private static bool Verify(SentinelDeviceKey deviceKey, string challenge, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(deviceKey.PublicKeySpki, out _);
            return key.VerifyData(Convert.FromBase64String(challenge), Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string SignInKey(Guid keyId) => $"sentinel-device-signin:{keyId:N}";

    private static string ChallengeKey(string username, string sessionId, Guid keyId) =>
        $"sentinel-device-unlock:{username.ToLowerInvariant()}:{sessionId}:{keyId:N}";

    private static SentinelDeviceKeyView ToView(SentinelDeviceKey key) => new(key.Id, key.Name, key.Storage, key.CreatedAtUnix, key.LastUsedAtUnix);
}
