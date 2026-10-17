using System.Security.Cryptography;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Tests;

// Page lock phase 3 + Touch ID sign-in: the Mac app's key protocol, with a real P-256 key standing
// in for the app's (Secure Enclave or Touch ID-gated key file - the server can't tell, by design).
public sealed class SentinelDeviceKeyTests
{
    private const string Session = "session-a";

    private static string Sign(ECDsa key, string challengeBase64) =>
        Convert.ToBase64String(key.SignData(Convert.FromBase64String(challengeBase64), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    [Fact]
    public async Task MacKey_ShouldUnlockLockedPages_OnlyWithAFreshValidSignature()
    {
        var f = await CreateAsync();
        using var mac = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = await f.Keys.RegisterAsync("grant", Session, "MacBook", Convert.ToBase64String(mac.ExportSubjectPublicKeyInfo()), SentinelDeviceKeys.File);

        var challenge = await f.Keys.CreateChallengeAsync("grant", Session, key.Id);
        var expires = await f.Keys.UnlockAsync("grant", Session, key.Id, Sign(mac, challenge));
        expires.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        (await f.Locks.IsUnlockedAsync("grant", Session)).Should().BeTrue();

        // Replay: the challenge was used up.
        await f.Locks.LockAllNowAsync("grant");
        await f.Keys.Invoking(k => k.UnlockAsync("grant", Session, key.Id, Sign(mac, challenge))).Should().ThrowAsync<SentinelLockException>();
        // Another key's signature over a fresh challenge.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fresh = await f.Keys.CreateChallengeAsync("grant", Session, key.Id);
        await f.Keys.Invoking(k => k.UnlockAsync("grant", Session, key.Id, Sign(stranger, fresh))).Should().ThrowAsync<SentinelLockException>();
        // Someone else can't use Grant's key, and a removed key stops working.
        await f.Keys.Invoking(k => k.CreateChallengeAsync("sam", Session, key.Id)).Should().ThrowAsync<SentinelLockException>();
        await f.Keys.RevokeAsync(key.Id, "grant");
        await f.Keys.Invoking(k => k.CreateChallengeAsync("grant", Session, key.Id)).Should().ThrowAsync<SentinelLockException>();
        (await f.Locks.IsUnlockedAsync("grant", Session)).Should().BeFalse();
    }

    [Fact]
    public async Task AddingAnotherWayToUnlock_ShouldNeedAnUnlockFirst()
    {
        var f = await CreateAsync();
        await f.Locks.SetPinAsync("grant", Session, null, "246810");
        using var mac = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(mac.ExportSubjectPublicKeyInfo());

        // A stolen session (no unlock) can't add its own key.
        await f.Keys.Invoking(k => k.RegisterAsync("grant", Session, "Thief", publicKey, SentinelDeviceKeys.File))
            .Should().ThrowAsync<SentinelLockException>().WithMessage("Unlock first*");

        await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null);
        (await f.Keys.RegisterAsync("grant", Session, "MacBook", publicKey, SentinelDeviceKeys.File)).Name.Should().Be("MacBook");
        await f.Keys.Invoking(k => k.RegisterAsync("grant", Session, "Bad", "not-a-key", SentinelDeviceKeys.File)).Should().ThrowAsync<SentinelLockException>();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await f.Keys.Invoking(k => k.RegisterAsync("grant", Session, "Bad", Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), SentinelDeviceKeys.File))
            .Should().ThrowAsync<SentinelLockException>();
    }

    [Fact]
    public async Task SignIn_ShouldNameTheKeysOwner_OnlyForAValidOneTimeSignature()
    {
        var f = await CreateAsync();
        using var mac = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = await f.Keys.RegisterAsync("grant", Session, "MacBook", Convert.ToBase64String(mac.ExportSubjectPublicKeyInfo()), SentinelDeviceKeys.File);

        var challenge = await f.Keys.CreateSignInChallengeAsync(key.Id);
        (await f.Keys.CompleteSignInAsync(key.Id, Sign(mac, challenge))).Should().Be("grant");
        (await f.Keys.CompleteSignInAsync(key.Id, Sign(mac, challenge))).Should().BeNull("one use per challenge");

        // An unknown key still gets a challenge (nothing to learn), but can't sign in.
        var unknown = Guid.NewGuid();
        var unknownChallenge = await f.Keys.CreateSignInChallengeAsync(unknown);
        unknownChallenge.Should().NotBeNullOrEmpty();
        (await f.Keys.CompleteSignInAsync(unknown, Sign(mac, unknownChallenge))).Should().BeNull();

        await f.Keys.RevokeAsync(key.Id, "grant");
        var afterRevoke = await f.Keys.CreateSignInChallengeAsync(key.Id);
        (await f.Keys.CompleteSignInAsync(key.Id, Sign(mac, afterRevoke))).Should().BeNull();
    }

    private sealed record Fixture(SentinelDeviceKeyService Keys, SentinelPageLockService Locks);

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var locks = new SentinelPageLockService(db, new SentinelAccessService(db),
            new DataProtectionPageLockCrypto(new EphemeralDataProtectionProvider()), TimeProvider.System);
        return new Fixture(new SentinelDeviceKeyService(db, locks, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System), locks);
    }
}
