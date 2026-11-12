using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// Warnings and errors grouped by cause (logging plan, Phase 1, 12-11-26): one row per
// fingerprint (source + message shape + exception type + first app frame), however often it
// happens. Times are Unix milliseconds, since SQLite can't sort DateTimeOffset.
public sealed class LogIssue : AuditableEntity
{
    public required string Fingerprint { get; set; }
    public required string Level { get; set; }
    public required string Origin { get; set; }
    public required string Source { get; set; }
    public required string Title { get; set; }
    public string? ExceptionType { get; set; }
    public string? SampleStack { get; set; }
    public int Count { get; set; }
    public long FirstSeenUnixMs { get; set; }
    public long LastSeenUnixMs { get; set; }
    public string FirstRelease { get; set; } = "unknown";
    public string LastRelease { get; set; } = "unknown";
    public string Status { get; set; } = LogIssueStatuses.Open;
    // Why it is Expected or Resolved (Phase 2 sets it: a known cause, or the fix's release).
    public string? StatusNote { get; set; }
}

// One sighting of an issue, with what ties it to the rest of the logs. Only the latest few per
// issue are kept (LogIssueLimits).
public sealed class LogOccurrence : AuditableEntity
{
    public Guid IssueId { get; set; }
    public long AtUnixMs { get; set; }
    public required string Message { get; set; }
    public string? TraceId { get; set; }
    public string? UserName { get; set; }
    public string? Page { get; set; }
    public string? CircuitId { get; set; }
    public string Release { get; set; } = "unknown";
}

public static class LogIssueStatuses
{
    public const string Open = "open";
    public const string Expected = "expected";
    public const string Resolved = "resolved";
}

public static class LogIssueLimits
{
    public const int OccurrencesPerIssue = 25;
    public const int RetentionDays = 30;
}
