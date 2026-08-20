using Discord;
using Discord.Rest;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Discord;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DiscordService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    ILogger<DiscordService> logger) : IDiscordService
{
    public async Task<DiscordConnectorSettingsView?> GetConnectorSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.DiscordConnectorSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        var (botToken, isUnreadable) = UnprotectBotToken(row.BotToken);
        return new DiscordConnectorSettingsView { BotToken = botToken, BotTokenUnreadable = isUnreadable };
    }

    public async Task SaveConnectorSettingsAsync(DiscordConnectorSettingsView settings, CancellationToken cancellationToken = default)
    {
        var row = await db.DiscordConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new DiscordConnectorSettings { Id = DiscordConnectorSettings.WellKnownId };
            db.DiscordConnectorSettings.Add(row);
        }

        row.BotToken = ProtectBotToken(settings.BotToken);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = "user";

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DiscordConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await CreateClientAsync(cancellationToken);
            if (client is null)
            {
                return new DiscordConnectionTestResult(false, null, "No Discord bot token is configured yet.");
            }

            return new DiscordConnectionTestResult(true, client.CurrentUser.Username, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord connection test failed.");
            return new DiscordConnectionTestResult(false, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<DiscordGuildView>> ListGuildsAsync(string search = "", CancellationToken cancellationToken = default)
    {
        using var client = await CreateClientAsync(cancellationToken);
        if (client is null) return [];

        var guilds = await client.GetGuildSummariesAsync().FlattenAsync();
        var query = guilds.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(g => g.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var views = new List<DiscordGuildView>();
        foreach (var guild in query)
        {
            views.Add(new DiscordGuildView(guild.Id, guild.Name, guild.IconUrl, null));
        }
        return views.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<DiscordChannelView>> ListChannelsAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        using var client = await CreateClientAsync(cancellationToken);
        if (client is null) return [];

        var guild = await client.GetGuildAsync(guildId, options: null);
        if (guild is null) return [];

        var channels = await guild.GetChannelsAsync();
        return channels
            .OfType<RestTextChannel>()
            .OrderBy(c => c.Position)
            .Select(c => new DiscordChannelView(c.Id, c.Name, "Text"))
            .ToList();
    }

    public async Task<IReadOnlyList<DiscordMessageView>> GetRecentMessagesAsync(ulong channelId, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var client = await CreateClientAsync(cancellationToken);
        if (client is null) return [];

        var channel = await client.GetChannelAsync(channelId);
        if (channel is not RestTextChannel textChannel) return [];

        var messages = await textChannel.GetMessagesAsync(Math.Clamp(limit, 1, 100)).FlattenAsync();
        return messages
            .OrderBy(m => m.Timestamp)
            .Select(m => new DiscordMessageView(m.Id, m.Author.Username, m.Author.GetAvatarUrl(), m.Content, m.Timestamp, m.Author.IsBot))
            .ToList();
    }

    public async Task<DiscordSendMessageResult> SendMessageAsync(ulong channelId, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new DiscordSendMessageResult(false, "Message content can't be empty.");
        }

        try
        {
            using var client = await CreateClientAsync(cancellationToken);
            if (client is null)
            {
                return new DiscordSendMessageResult(false, "No Discord bot token is configured yet.");
            }

            var channel = await client.GetChannelAsync(channelId);
            if (channel is not RestTextChannel textChannel)
            {
                return new DiscordSendMessageResult(false, "That channel can't receive messages.");
            }

            await textChannel.SendMessageAsync(content.Trim());
            return new DiscordSendMessageResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send a Discord message to channel {ChannelId}.", channelId);
            return new DiscordSendMessageResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<DiscordMemberView>> ListMembersAsync(ulong guildId, string search = "", CancellationToken cancellationToken = default)
    {
        using var client = await CreateClientAsync(cancellationToken);
        if (client is null) return [];

        var guild = await client.GetGuildAsync(guildId, options: null);
        if (guild is null) return [];

        var roleNamesById = guild.Roles.ToDictionary(r => r.Id, r => r.Name);
        var users = await guild.GetUsersAsync().FlattenAsync();

        var query = users.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u =>
                u.Username.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (u.Nickname is not null && u.Nickname.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return query
            .Select(u => new DiscordMemberView(
                u.Id,
                u.Username,
                u.Nickname,
                u.GetAvatarUrl(),
                u.RoleIds.Where(id => roleNamesById.ContainsKey(id)).Select(id => roleNamesById[id]).ToList()))
            .OrderBy(m => m.DisplayName ?? m.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<DiscordRoleView>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        using var client = await CreateClientAsync(cancellationToken);
        if (client is null) return [];

        var guild = await client.GetGuildAsync(guildId, options: null);
        if (guild is null) return [];

        return guild.Roles
            .OrderByDescending(r => r.Position)
            .Select(r => new DiscordRoleView(r.Id, r.Name, $"#{r.Colors.PrimaryColor.RawValue:X6}", r.Position))
            .ToList();
    }

    private async Task<DiscordRestClient?> CreateClientAsync(CancellationToken cancellationToken)
    {
        var row = await db.DiscordConnectorSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.BotToken)) return null;

        var (botToken, isUnreadable) = UnprotectBotToken(row.BotToken);
        if (isUnreadable || string.IsNullOrWhiteSpace(botToken)) return null;

        var client = new DiscordRestClient();
        await client.LoginAsync(TokenType.Bot, botToken);
        return client;
    }

    private string ProtectBotToken(string botToken) =>
        string.IsNullOrWhiteSpace(botToken) ? string.Empty : secretProtector.Protect(botToken.Trim());

    private (string BotToken, bool IsUnreadable) UnprotectBotToken(string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue)) return (string.Empty, false);

        try
        {
            return (secretProtector.Unprotect(storedValue), false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to decrypt the stored Discord bot token. The key ring may have changed since it was saved.");
            return (string.Empty, true);
        }
    }
}
