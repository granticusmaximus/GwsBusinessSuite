namespace GwsBusinessSuite.Application.Discord;

public interface IDiscordService
{
    Task<DiscordConnectorSettingsView?> GetConnectorSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveConnectorSettingsAsync(DiscordConnectorSettingsView settings, CancellationToken cancellationToken = default);
    Task<DiscordConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscordGuildView>> ListGuildsAsync(string search = "", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscordChannelView>> ListChannelsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscordMessageView>> GetRecentMessagesAsync(ulong channelId, int limit = 50, CancellationToken cancellationToken = default);
    Task<DiscordSendMessageResult> SendMessageAsync(ulong channelId, string content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscordMemberView>> ListMembersAsync(ulong guildId, string search = "", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscordRoleView>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken = default);
}
