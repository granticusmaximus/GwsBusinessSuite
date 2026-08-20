namespace GwsBusinessSuite.Application.Discord;

public sealed class DiscordConnectorSettingsView
{
    public string BotToken { get; set; } = string.Empty;
    public bool BotTokenUnreadable { get; set; }
}

public sealed record DiscordConnectionTestResult(bool Success, string? BotUsername, string? ErrorMessage);

public sealed record DiscordGuildView(ulong Id, string Name, string? IconUrl, int? ApproximateMemberCount);

public sealed record DiscordChannelView(ulong Id, string Name, string Type);

public sealed record DiscordMessageView(ulong Id, string AuthorName, string? AuthorAvatarUrl, string Content, DateTimeOffset SentAt, bool IsBot);

public sealed record DiscordMemberView(ulong Id, string Username, string? DisplayName, string? AvatarUrl, IReadOnlyList<string> RoleNames);

public sealed record DiscordRoleView(ulong Id, string Name, string ColorHex, int Position);

public sealed record DiscordSendMessageResult(bool Success, string? ErrorMessage);
