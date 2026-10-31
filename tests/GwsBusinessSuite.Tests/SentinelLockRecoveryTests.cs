using System.Security.Cryptography;
using FluentAssertions;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Tests;

// The way back in (31-10-26): Grant's only unlock method was a Mac key the app had lost, with no
// PIN, so setting up a PIN or a new key ("Unlock first") went round in a circle. The authenticator
// code unlocks instead; wrong codes are limited like the PIN.
public sealed class SentinelLockRecoveryTests
{
    private const string Session = "session-a";
    private const string GoodCode = "123456";

    [Fact]
    public async Task WithOnlyALostMacKey_TheAuthenticatorCode_ShouldLetAPinAndANewKeyBeAdded()
    {
        var f = await CreateAsync();
        // The old Mac key: registered while it was the first way to unlock, then lost on the Mac.
        await f.Keys.RegisterAsync("grant", Session, "Old MacBook", NewPublicKey(), SentinelDeviceKeys.File);
        await f.Locks.LockSessionNowAsync("grant", Session);

        await f.Locks.Invoking(l => l.SetPinAsync("grant", Session, null, "246810")).Should().ThrowAsync<SentinelLockException>().WithMessage("Unlock first*");
        await f.Keys.Invoking(k => k.RegisterAsync("grant", Session, "MacBook", NewPublicKey(), SentinelDeviceKeys.File))
            .Should().ThrowAsync<SentinelLockException>().WithMessage("Unlock first*authenticator code*");

        await f.Recovery.Invoking(r => r.UnlockWithAuthenticatorAsync("grant", Session, "000000")).Should().ThrowAsync<SentinelLockException>().WithMessage("That code isn't right*");
        (await f.Locks.IsUnlockedAsync("grant", Session)).Should().BeFalse();

        await f.Recovery.UnlockWithAuthenticatorAsync("grant", Session, GoodCode);

        (await f.Locks.IsUnlockedAsync("grant", Session)).Should().BeTrue();
        (await f.Locks.IsUnlockedAsync("grant", "another-session")).Should().BeFalse("only the session that gave the code");
        (await f.Keys.RegisterAsync("grant", Session, "MacBook", NewPublicKey(), SentinelDeviceKeys.File)).Name.Should().Be("MacBook");
        await f.Locks.SetPinAsync("grant", Session, null, "246810");
        (await f.Locks.GetMethodsAsync("grant")).HasPin.Should().BeTrue();
    }

    [Fact]
    public async Task FiveWrongCodes_ShouldPauseIt_EvenForTheRightCode_UntilTheWindowPasses()
    {
        var f = await CreateAsync();
        for (var i = 0; i < SentinelPageLock.MaxPinFailures; i++)
            await f.Recovery.Invoking(r => r.UnlockWithAuthenticatorAsync("grant", Session, "000000")).Should().ThrowAsync<SentinelLockException>();

        await f.Recovery.Invoking(r => r.UnlockWithAuthenticatorAsync("GRANT", Session, GoodCode)).Should().ThrowAsync<SentinelLockException>().WithMessage("Too many wrong codes*");
        f.Users.Checked.Should().Be(SentinelPageLock.MaxPinFailures, "a paused person's codes aren't even checked");

        f.Time.Advance(SentinelPageLock.PinFailureWindow + TimeSpan.FromSeconds(1));
        await f.Recovery.UnlockWithAuthenticatorAsync("grant", Session, GoodCode);
        (await f.Locks.IsUnlockedAsync("grant", Session)).Should().BeTrue();
    }

    [Fact]
    public async Task AnAccountWithoutAnAuthenticator_ShouldBeToldSo()
    {
        var f = await CreateAsync();

        await f.Recovery.Invoking(r => r.UnlockWithAuthenticatorAsync("sam", Session, GoodCode)).Should().ThrowAsync<SentinelLockException>().WithMessage("*no authenticator*");
        f.Users.Checked.Should().Be(0);
    }

    private static string NewPublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private sealed record Fixture(SentinelPageLockService Locks, SentinelDeviceKeyService Keys, SentinelLockRecoveryService Recovery, Users Users, ManualTime Time);

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var locks = new SentinelPageLockService(db, new SentinelAccessService(db), new DataProtectionPageLockCrypto(new EphemeralDataProtectionProvider()), time);
        var users = new Users();
        return new Fixture(locks, new SentinelDeviceKeyService(db, locks, cache, time), new SentinelLockRecoveryService(locks, users, cache, time), users, time);
    }

    // Grant has an authenticator (its current code is GoodCode); Sam has none.
    private sealed class Users : IUserManagementService
    {
        private readonly UserView _grant = new() { Id = Guid.NewGuid(), Username = "grant", IsActive = true, MfaEnabled = true };
        private readonly UserView _sam = new() { Id = Guid.NewGuid(), Username = "sam", IsActive = true, MfaEnabled = false };
        public int Checked { get; private set; }

        public Task<IReadOnlyList<UserView>> ListUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserView>>([_grant, _sam]);

        public Task<MfaVerificationResult> VerifyMfaAsync(Guid userId, string code, CancellationToken cancellationToken = default)
        {
            Checked++;
            return Task.FromResult(userId == _grant.Id && code == GoodCode ? new MfaVerificationResult(true, _grant) : new MfaVerificationResult(false, null));
        }

        public Task<UserManagementResult> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserManagementResult> ChangeRoleAsync(Guid userId, string newRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserManagementResult> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserManagementResult> ToggleActiveAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginAttemptResult> AttemptLoginAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginAttemptResult?> AttemptDeviceLoginAsync(string providedSecret, string configuredSecret, string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MfaEnrollment?> PrepareMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MfaEnrollmentResult> CompleteMfaEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserManagementResult> UnlockUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
