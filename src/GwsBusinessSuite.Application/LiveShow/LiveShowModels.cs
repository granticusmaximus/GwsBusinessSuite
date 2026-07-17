namespace GwsBusinessSuite.Application.LiveShow;

public sealed record LiveShowSessionView(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string InviteToken,
    DateTimeOffset InviteExpiresAt);

public sealed record LiveShowRecordingView(
    Guid Id,
    Guid SessionId,
    string SessionTitle,
    string FileName,
    int DurationSeconds,
    long FileSizeBytes,
    DateTimeOffset CreatedAt);

public sealed record LiveShowIceServerView(
    IReadOnlyList<string> Urls,
    string? Username = null,
    string? Credential = null);

public sealed record LiveShowIceConfiguration(
    IReadOnlyList<LiveShowIceServerView> IceServers,
    bool IsTurnConfigured,
    DateTimeOffset? TurnCredentialsExpireAt);
