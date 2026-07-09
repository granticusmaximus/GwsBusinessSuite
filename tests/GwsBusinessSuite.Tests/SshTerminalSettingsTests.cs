using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class SshTerminalSettingsTests
{
    [Fact]
    public async Task SaveSshSettingsAsync_ShouldNeverReturnThePrivateKey_ThroughGetSettingsAsync()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        await service.SaveSshSettingsAsync(new SshSettingsInput
        {
            Username = "deploy",
            Port = 2222,
            NewPrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc123\n-----END OPENSSH PRIVATE KEY-----"
        });

        var stored = await db.DigitalOceanSettings.AsNoTracking().SingleAsync();
        stored.SshPrivateKey.Should().StartWith("enc::");

        var settings = await service.GetSettingsAsync();
        settings.Should().NotBeNull();
        settings!.SshUsername.Should().Be("deploy");
        settings.SshPort.Should().Be(2222);
        settings.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public async Task SaveSshSettingsAsync_WithBlankNewPrivateKey_ShouldLeaveExistingKeyUntouched()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        await service.SaveSshSettingsAsync(new SshSettingsInput { Username = "root", Port = 22, NewPrivateKey = "original-key" });
        var originalEncrypted = (await db.DigitalOceanSettings.AsNoTracking().SingleAsync()).SshPrivateKey;

        await service.SaveSshSettingsAsync(new SshSettingsInput { Username = "root", Port = 22, NewPrivateKey = null });

        var reloaded = await db.DigitalOceanSettings.AsNoTracking().SingleAsync();
        reloaded.SshPrivateKey.Should().Be(originalEncrypted);

        var settings = await service.GetSettingsAsync();
        settings!.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public async Task SaveSshSettingsAsync_WithClearPrivateKeyTrue_ShouldRemoveTheKey()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        await service.SaveSshSettingsAsync(new SshSettingsInput { Username = "root", Port = 22, NewPrivateKey = "some-key" });

        await service.SaveSshSettingsAsync(new SshSettingsInput { Username = "root", Port = 22, ClearPrivateKey = true });

        var settings = await service.GetSettingsAsync();
        settings!.HasPrivateKey.Should().BeFalse();
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldReportSshPrivateKeyUnreadable_WhenDecryptionFails()
    {
        // A value stored without the expected "enc::" prefix can never be decrypted, so it
        // must be surfaced as unreadable rather than throwing out of GetSettingsAsync.
        await using var db = await CreateDbAsync();
        db.DigitalOceanSettings.Add(new GwsBusinessSuite.Domain.Entities.DigitalOceanSettings
        {
            SshUsername = "root",
            SshPort = 22,
            SshPrivateKey = "legacy-plaintext-key"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var settings = await service.GetSettingsAsync();

        settings.Should().NotBeNull();
        settings!.HasPrivateKey.Should().BeTrue();
        settings.SshPrivateKeyUnreadable.Should().BeTrue();
    }

    private static DigitalOceanService CreateService(ApplicationDbContext db)
    {
        var client = new HttpClient(new NeverCalledHandler()) { BaseAddress = new Uri("https://api.digitalocean.com/v2/") };
        return new DigitalOceanService(client, db, new ThrowingFakeSecretProtector(), NullLogger<DigitalOceanService>.Instance);
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("These tests only exercise settings persistence and should never call the DO API.");
    }

    // Mirrors CjAdsServiceTests' FakeSecretProtector - throws on anything not carrying the
    // expected "enc::" prefix, so "unreadable" can be simulated by seeding a raw string.
    private sealed class ThrowingFakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => string.IsNullOrWhiteSpace(plaintext) ? string.Empty : $"enc::{plaintext}";

        public string Unprotect(string protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return string.Empty;
            }

            if (!protectedValue.StartsWith("enc::", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Value is not encrypted with expected prefix.");
            }

            return protectedValue["enc::".Length..];
        }
    }
}
