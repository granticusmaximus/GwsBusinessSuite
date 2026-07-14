using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Covers OpenAsync's pre-connection guard clauses, which run entirely against the DB
// and a fake IDigitalOceanService - no live network/SSH handshake involved. The actual
// live-connect and host-key-mismatch-during-handshake paths (client.Connect() and the
// HostKeyReceived callback) depend on Renci.SshNet's concrete, non-fakeable SshClient
// talking to a real server, so they aren't covered here; IsHostKeyTrusted (the pure
// accept/reject decision those paths rely on) is covered separately in
// SshHostKeyPinningTests.
public sealed class SshTerminalServiceTests
{
    [Fact]
    public async Task OpenAsync_ShouldFail_WhenNoPrivateKeyIsSaved()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new NeverCalledDigitalOceanService());

        var result = await service.OpenAsync("grantwatson", 80, 24);

        result.Succeeded.Should().BeFalse();
        result.Session.Should().BeNull();
        result.FailureReason.Should().Contain("No SSH private key saved yet");
        db.DockerActionLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_ShouldFail_WhenStoredPrivateKeyCannotBeDecrypted()
    {
        await using var db = await CreateDbAsync();
        db.DigitalOceanSettings.Add(new DigitalOceanSettings
        {
            SshUsername = "root",
            SshPort = 22,
            SshPrivateKey = "not-encrypted-with-expected-prefix"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new NeverCalledDigitalOceanService());

        var result = await service.OpenAsync("grantwatson", 80, 24);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("could not be decrypted");
        db.DockerActionLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_ShouldFail_WhenDropletHasNoPublicIpYet()
    {
        await using var db = await CreateDbAsync();
        db.DigitalOceanSettings.Add(new DigitalOceanSettings
        {
            SshUsername = "root",
            SshPort = 22,
            SshPrivateKey = "enc::-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----"
        });
        await db.SaveChangesAsync();

        var droplet = new FakeDigitalOceanService
        {
            DropletResult = new DropletInfoResult(false, null, "The droplet isn't connected yet.")
        };
        var service = CreateService(db, droplet);

        var result = await service.OpenAsync("grantwatson", 80, 24);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Be("The droplet isn't connected yet.");
    }

    [Fact]
    public async Task OpenAsync_ShouldFail_WithInvalidPrivateKeyFormat_BeforeAttemptingAnyNetworkConnection()
    {
        await using var db = await CreateDbAsync();
        db.DigitalOceanSettings.Add(new DigitalOceanSettings
        {
            SshUsername = "root",
            SshPort = 22,
            SshPrivateKey = "enc::this is not a valid PEM-encoded private key at all"
        });
        await db.SaveChangesAsync();

        var droplet = new FakeDigitalOceanService
        {
            DropletResult = new DropletInfoResult(true, new DropletInfoView { Name = "test-droplet", Status = "active", PublicIpAddress = "203.0.113.10" })
        };
        var service = CreateService(db, droplet);

        var result = await service.OpenAsync("grantwatson", 80, 24);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid private key");
    }

    [Fact]
    public async Task TrustHostKeyAsync_ShouldPinFingerprint_AttributedToUser()
    {
        await using var db = await CreateDbAsync();
        db.DigitalOceanSettings.Add(new DigitalOceanSettings { SshUsername = "root", SshPort = 22 });
        await db.SaveChangesAsync();

        var service = CreateService(db, new NeverCalledDigitalOceanService());
        await service.TrustHostKeyAsync("SHA256:newfingerprint");

        var row = await db.DigitalOceanSettings.AsNoTracking().SingleAsync();
        row.SshHostKeyFingerprint.Should().Be("SHA256:newfingerprint");
        row.UpdatedBy.Should().Be("user");
    }

    private static SshTerminalService CreateService(ApplicationDbContext db, IDigitalOceanService digitalOceanService) =>
        new(db, digitalOceanService, new PassthroughSecretProtector(), NullLogger<SshTerminalService>.Instance);

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    // Strips the "enc::" test-fixture prefix so a stored value round-trips as the plain
    // key text the SSH.NET PrivateKeyFile parser then rejects as invalid PEM.
    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"enc::{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("enc::", StringComparison.Ordinal)
                ? protectedValue["enc::".Length..]
                : throw new InvalidOperationException("Value is not encrypted with expected prefix.");
    }

    private sealed class FakeDigitalOceanService : IDigitalOceanService
    {
        public DropletInfoResult DropletResult { get; set; } = new(false, null, "Not configured.");

        public Task<DigitalOceanSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(DigitalOceanApiSettingsInput settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSshSettingsAsync(SshSettingsInput settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DropletInfoResult> GetDropletInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DropletResult);

        public Task<IReadOnlyList<DropletActionView>> ListRecentActionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DigitalOceanActionResult> RebootDropletAsync(string performedBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DigitalOceanActionResult> ResizeDropletAsync(string newSize, bool resizeDisk, string performedBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DigitalOceanActionResult> CreateSnapshotAsync(string snapshotName, string performedBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // Used when a code path should short-circuit before ever needing droplet info at
    // all (e.g. no private key saved yet) - any call here is itself a test failure.
    private sealed class NeverCalledDigitalOceanService : IDigitalOceanService
    {
        public Task<DigitalOceanSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task SaveSettingsAsync(DigitalOceanApiSettingsInput settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task SaveSshSettingsAsync(SshSettingsInput settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task<DropletInfoResult> GetDropletInfoAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task<IReadOnlyList<DropletActionView>> ListRecentActionsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task<DigitalOceanActionResult> RebootDropletAsync(string performedBy, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task<DigitalOceanActionResult> ResizeDropletAsync(string newSize, bool resizeDisk, string performedBy, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");

        public Task<DigitalOceanActionResult> CreateSnapshotAsync(string snapshotName, string performedBy, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called.");
    }
}
