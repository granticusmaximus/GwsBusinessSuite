using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

public static class ThreatAssetKinds
{
    public const string Domain = "domain";
    public const string Ip = "ip";
}

// A domain or IP the business owns, checked on a schedule by ThreatMonitorBackgroundService.
// SnapshotJson holds the last observed state (certificate expiry, CT hostnames, open ports...)
// so the next check reports only what changed.
public sealed class ThreatWatchAsset : AuditableEntity
{
    public required string Value { get; set; }
    public required string Kind { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? SnapshotJson { get; set; }
    public string? LastError { get; set; }
}

// One piece of software the business runs (e.g. "Ollama"), matched against CISA KEV and
// recently published NVD CVEs.
public sealed class ThreatStackItem : AuditableEntity
{
    public required string Name { get; set; }
    public string? Vendor { get; set; }
    public string? Product { get; set; }
    // Comma-separated NVD keyword searches; defaults to Name when empty.
    public string? Keywords { get; set; }
}

public static class ThreatFindingSources
{
    public const string Asset = "asset";
    public const string EmailSecurity = "email";
    public const string Stack = "stack";
    public const string Dependency = "dependency";
}

public static class ThreatFindingSeverities
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    public const string Info = "info";
}

// Something the monitor found. Key is unique so re-running a check never duplicates a finding;
// ResolvedAt is set when a later check no longer sees it.
public sealed class ThreatFinding : AuditableEntity
{
    public required string Key { get; set; }
    public required string Source { get; set; }
    public required string Subject { get; set; }
    public required string Severity { get; set; }
    public required string Title { get; set; }
    public string? Detail { get; set; }
    public string? ReferenceUrl { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? DigestedAt { get; set; }
}

// A saved Threat Intelligence lookup, so an investigation can be reopened or exported later.
public sealed class ThreatInvestigation : AuditableEntity
{
    public required string Query { get; set; }
    public required string Kind { get; set; }
    public required string Summary { get; set; }
    public required string ResultJson { get; set; }
    public Guid? ExportedWikiPageId { get; set; }
}

// Single row: monitor schedule state and the daily digest settings.
public sealed class ThreatMonitorSettings : AuditableEntity
{
    public bool DigestEnabled { get; set; }
    public string? DigestRecipient { get; set; }
    public int DigestHourLocal { get; set; } = 7;
    public DateTimeOffset? LastDigestSentAt { get; set; }
    public DateTimeOffset? LastAssetCheckAt { get; set; }
    public DateTimeOffset? LastStackCheckAt { get; set; }
    public DateTimeOffset? LastDependencyCheckAt { get; set; }
}

// Per-admin "new since your last visit" marker for the KEV list.
public sealed class ThreatIntelUserState : AuditableEntity
{
    public required string Username { get; set; }
    public DateTimeOffset? LastKevVisitAt { get; set; }
}
