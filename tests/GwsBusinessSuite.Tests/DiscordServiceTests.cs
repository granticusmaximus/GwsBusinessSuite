using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Discord;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DiscordServiceTests
{
    [Fact]
    public async Task SaveConnectorSettingsAsync_ShouldPersistEncryptedBotToken_AndReturnDecryptedValue()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        await service.SaveConnectorSettingsAsync(new DiscordConnectorSettingsView { BotToken = "bot-token-123" });

        var stored = await db.DiscordConnectorSettings.AsNoTracking().SingleAsync();
        Assert.Equal("enc::bot-token-123", stored.BotToken);

        var reloaded = await service.GetConnectorSettingsAsync();
        Assert.NotNull(reloaded);
        Assert.Equal("bot-token-123", reloaded!.BotToken);
        Assert.False(reloaded.BotTokenUnreadable);
    }

    [Fact]
    public async Task SaveConnectorSettingsAsync_CalledTwice_ShouldUpdateTheSameSingletonRow()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        await service.SaveConnectorSettingsAsync(new DiscordConnectorSettingsView { BotToken = "first-token" });
        await service.SaveConnectorSettingsAsync(new DiscordConnectorSettingsView { BotToken = "second-token" });

        var rows = await db.DiscordConnectorSettings.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(GwsBusinessSuite.Domain.Entities.DiscordConnectorSettings.WellKnownId, rows[0].Id);

        var reloaded = await service.GetConnectorSettingsAsync();
        Assert.Equal("second-token", reloaded!.BotToken);
    }

    [Fact]
    public async Task GetConnectorSettingsAsync_ShouldFlagUndecryptableBotTokenAsUnreadable()
    {
        // A value stored without the expected "enc::" prefix can never be decrypted (whether
        // it's legacy plaintext or ciphertext from a rotated key ring), so it must be surfaced
        // as unreadable rather than returned as a usable token.
        await using var db = await CreateDbAsync();
        db.DiscordConnectorSettings.Add(new GwsBusinessSuite.Domain.Entities.DiscordConnectorSettings
        {
            Id = GwsBusinessSuite.Domain.Entities.DiscordConnectorSettings.WellKnownId,
            BotToken = "legacy-plain"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());
        var settings = await service.GetConnectorSettingsAsync();

        Assert.NotNull(settings);
        Assert.Equal(string.Empty, settings!.BotToken);
        Assert.True(settings.BotTokenUnreadable);
    }

    [Fact]
    public async Task GetConnectorSettingsAsync_ShouldReturnNull_WhenNothingHasEverBeenSaved()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var settings = await service.GetConnectorSettingsAsync();

        Assert.Null(settings);
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldFailGracefully_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var result = await service.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Null(result.BotUsername);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ListGuildsAsync_ShouldReturnEmpty_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var guilds = await service.ListGuildsAsync();

        Assert.Empty(guilds);
    }

    [Fact]
    public async Task ListChannelsAsync_ShouldReturnEmpty_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var channels = await service.ListChannelsAsync(123UL);

        Assert.Empty(channels);
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ShouldReturnEmpty_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var messages = await service.GetRecentMessagesAsync(123UL);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldFailGracefully_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var result = await service.SendMessageAsync(123UL, "hello");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldRejectEmptyContent_WithoutTouchingDiscordAtAll()
    {
        await using var db = await CreateDbAsync();
        // Even with a (fake, unusable) token configured, empty content should be rejected
        // before any attempt to reach Discord's API.
        await db.DiscordConnectorSettings.AddAsync(new GwsBusinessSuite.Domain.Entities.DiscordConnectorSettings
        {
            Id = GwsBusinessSuite.Domain.Entities.DiscordConnectorSettings.WellKnownId,
            BotToken = "enc::not-a-real-token"
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var result = await service.SendMessageAsync(123UL, "   ");

        Assert.False(result.Success);
        Assert.Equal("Message content can't be empty.", result.ErrorMessage);
    }

    [Fact]
    public async Task ListMembersAsync_ShouldReturnEmpty_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var members = await service.ListMembersAsync(123UL);

        Assert.Empty(members);
    }

    [Fact]
    public async Task ListRolesAsync_ShouldReturnEmpty_WhenNoTokenIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        var roles = await service.ListRolesAsync(123UL);

        Assert.Empty(roles);
    }

    private static DiscordService CreateService(ApplicationDbContext db, ISecretProtector secretProtector)
    {
        return new DiscordService(db, secretProtector, NullLogger<DiscordService>.Instance);
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

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext)
        {
            return string.IsNullOrWhiteSpace(plaintext) ? string.Empty : $"enc::{plaintext}";
        }

        public string Unprotect(string protectedValue)
        {
            if (!protectedValue.StartsWith("enc::", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Value was not protected by this protector.");
            }

            return protectedValue["enc::".Length..];
        }
    }
}
