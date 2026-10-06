namespace GwsBusinessSuite.Application.ThreatIntel;

// "My exposure": continuous checks of what the business itself owns and runs - its domains and
// IPs, the software it depends on, and this app's own packages - plus the daily digest email.
public interface IThreatMonitorService
{
    Task<IReadOnlyList<WatchAssetView>> ListAssetsAsync(CancellationToken cancellationToken = default);
    Task<WatchAssetView> AddAssetAsync(string value, string? label, string username, CancellationToken cancellationToken = default);
    Task RemoveAssetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StackItemView>> ListStackAsync(CancellationToken cancellationToken = default);
    Task<StackItemView> AddStackItemAsync(string name, string? vendor, string? product, string? keywords, string username,
        CancellationToken cancellationToken = default);
    Task RemoveStackItemAsync(Guid id, CancellationToken cancellationToken = default);

    IReadOnlyList<DependencyPackage> ListDependencies();

    Task<IReadOnlyList<ThreatFindingView>> ListFindingsAsync(bool includeResolved = false, CancellationToken cancellationToken = default);
    Task AcknowledgeFindingAsync(Guid id, string username, CancellationToken cancellationToken = default);

    Task<ThreatMonitorSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(bool digestEnabled, string? digestRecipient, int digestHourLocal, CancellationToken cancellationToken = default);

    Task<ThreatCheckResult> RunChecksAsync(ThreatCheckScope scope, CancellationToken cancellationToken = default);

    // Emails the new (not yet digested) findings plus the last day's KEV additions. Returns false
    // when there was nothing to send or no recipient/mail route.
    Task<DigestSendResult> SendDigestAsync(bool force, CancellationToken cancellationToken = default);
}

[Flags]
public enum ThreatCheckScope
{
    None = 0,
    Assets = 1,
    Stack = 2,
    Dependencies = 4,
    All = Assets | Stack | Dependencies
}

public sealed record WatchAssetView(Guid Id, string Value, string Kind, string? Label, DateTimeOffset? LastCheckedAt, string? LastError, int OpenFindings);

public sealed record StackItemView(Guid Id, string Name, string? Vendor, string? Product, string? Keywords, int OpenFindings);

public sealed record DependencyPackage(string Ecosystem, string Name, string Version);

public sealed record ThreatFindingView(
    Guid Id, string Source, string Subject, string Severity, string Title, string? Detail, string? ReferenceUrl,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? ResolvedAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

public sealed record ThreatMonitorSettingsView(
    bool DigestEnabled, string? DigestRecipient, int DigestHourLocal, DateTimeOffset? LastDigestSentAt,
    DateTimeOffset? LastAssetCheckAt, DateTimeOffset? LastStackCheckAt, DateTimeOffset? LastDependencyCheckAt, bool CanSendEmail);

public sealed record ThreatCheckResult(int NewFindings, int ResolvedFindings, IReadOnlyList<string> Errors);

public sealed record DigestSendResult(bool Sent, string Message);

// A finding a check produced this run, before it's matched against stored findings.
public sealed record FindingCandidate(string Key, string Severity, string Title, string? Detail, string? ReferenceUrl);
