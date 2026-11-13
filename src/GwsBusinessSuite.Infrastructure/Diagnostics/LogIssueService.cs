using System.Text.Json;
using GwsBusinessSuite.Application.Diagnostics;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Diagnostics;

// The issues store (logging plan, Phase 1, 12-11-26). A context per call: the ingest loop, the
// logs page and the prune all reach it at once.
public sealed class LogIssueService(IDbContextFactory<ApplicationDbContext> dbFactory) : ILogIssueService
{
    public async Task RecordAsync(IReadOnlyList<CapturedLogEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;
        var groups = events.GroupBy(LogFingerprint.For).ToList();
        var fingerprints = groups.Select(group => group.Key).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.LogIssues.Where(issue => fingerprints.Contains(issue.Fingerprint)).ToDictionaryAsync(issue => issue.Fingerprint, cancellationToken);
        var touched = new List<LogIssue>();
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(captured => captured.At).ToList();
            var first = ordered[0];
            var last = ordered[^1];
            if (!existing.TryGetValue(group.Key, out var issue))
            {
                issue = new LogIssue
                {
                    Fingerprint = group.Key,
                    Level = first.Level,
                    Origin = first.Origin,
                    Source = Cap(first.Source, 200)!,
                    Title = Cap(TitleOf(first), 500)!,
                    ExceptionType = Cap(first.ExceptionType, 200),
                    SampleStack = Cap(first.Exception ?? first.ClientStack, 8000),
                    FirstSeenUnixMs = first.At.ToUnixTimeMilliseconds(),
                    FirstRelease = Cap(first.Release, 40)!,
                };
                db.LogIssues.Add(issue);
            }
            if (issue.Status == LogIssueStatuses.Open
                && KnownCauses.Match(issue.ExceptionType, issue.Source, issue.Title, issue.SampleStack) is { } knownCause)
            {
                issue.Status = LogIssueStatuses.Expected;
                issue.StatusNote = knownCause;
            }
            else if (issue.Status == LogIssueStatuses.Resolved)
            {
                // It came back after being marked fixed.
                issue.Status = LogIssueStatuses.Open;
                issue.StatusNote = $"Came back in release {last.Release} after being marked resolved.";
            }
            issue.Count += ordered.Count;
            issue.LastSeenUnixMs = Math.Max(issue.LastSeenUnixMs, last.At.ToUnixTimeMilliseconds());
            issue.LastRelease = Cap(last.Release, 40)!;
            if (string.Compare(last.Level, issue.Level, StringComparison.Ordinal) != 0 && last.Level is "Error" or "Fatal") issue.Level = last.Level;
            touched.Add(issue);
            foreach (var captured in ordered.TakeLast(LogIssueLimits.OccurrencesPerIssue))
            {
                db.LogOccurrences.Add(new LogOccurrence
                {
                    IssueId = issue.Id,
                    AtUnixMs = captured.At.ToUnixTimeMilliseconds(),
                    Message = Cap(captured.ClientMessage ?? captured.Message, 2000)!,
                    TraceId = Cap(captured.TraceId, 32),
                    UserName = Cap(captured.UserName, 100),
                    Page = Cap(captured.Page, 300),
                    CircuitId = Cap(captured.CircuitId, 16),
                    Release = Cap(captured.Release, 40)!,
                });
            }
        }
        await db.SaveChangesAsync(cancellationToken);

        // Keep the latest few sightings per issue.
        var ids = touched.Select(issue => issue.Id).ToList();
        var stale = (await db.LogOccurrences.Where(occurrence => ids.Contains(occurrence.IssueId))
                .Select(occurrence => new { occurrence.Id, occurrence.IssueId, occurrence.AtUnixMs }).ToListAsync(cancellationToken))
            .GroupBy(occurrence => occurrence.IssueId)
            .SelectMany(group => group.OrderByDescending(occurrence => occurrence.AtUnixMs).Skip(LogIssueLimits.OccurrencesPerIssue).Select(occurrence => occurrence.Id))
            .ToList();
        if (stale.Count > 0) await db.LogOccurrences.Where(occurrence => stale.Contains(occurrence.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LogIssueSummary>> ListAsync(LogIssueQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var issues = db.LogIssues.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Origin)) issues = issues.Where(issue => issue.Origin == query.Origin);
        if (!string.IsNullOrWhiteSpace(query.Status)) issues = issues.Where(issue => issue.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Release)) issues = issues.Where(issue => issue.FirstRelease == query.Release);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            issues = issues.Where(issue => EF.Functions.Like(issue.Title, $"%{search}%") || EF.Functions.Like(issue.Source, $"%{search}%")
                || (issue.ExceptionType != null && EF.Functions.Like(issue.ExceptionType, $"%{search}%")));
        }
        var rows = await issues.OrderByDescending(issue => issue.LastSeenUnixMs).Take(Math.Clamp(query.Take, 1, 1000)).ToListAsync(cancellationToken);
        return rows.Select(Summary).ToList();
    }

    public async Task<LogIssueDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var issue = await db.LogIssues.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (issue is null) return null;
        var occurrences = await db.LogOccurrences.AsNoTracking().Where(occurrence => occurrence.IssueId == id)
            .OrderByDescending(occurrence => occurrence.AtUnixMs).Take(LogIssueLimits.OccurrencesPerIssue).ToListAsync(cancellationToken);
        return new LogIssueDetail(Summary(issue), issue.SampleStack, occurrences.Select(occurrence => new LogOccurrenceView(
            DateTimeOffset.FromUnixTimeMilliseconds(occurrence.AtUnixMs), occurrence.Message, occurrence.TraceId, occurrence.UserName,
            occurrence.Page, occurrence.CircuitId, occurrence.Release)).ToList());
    }

    public async Task SetStatusAsync(Guid id, string status, string? note, CancellationToken cancellationToken = default)
    {
        if (status is not (LogIssueStatuses.Open or LogIssueStatuses.Expected or LogIssueStatuses.Resolved))
            throw new ArgumentException($"Unknown status '{status}'.", nameof(status));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var issue = await db.LogIssues.FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That issue no longer exists.");
        issue.Status = status;
        issue.StatusNote = Cap(note?.Trim() is { Length: > 0 } text ? text : null, 1000);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Logging Phase 2, part 5 (13-11-26): an open issue quiet this long closes itself; it reopens as
    // "came back" if it's seen again (RecordAsync).
    public const int QuietDaysToClose = 7;

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now.AddDays(-LogIssueLimits.RetentionDays).ToUnixTimeMilliseconds();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var quietSince = now.AddDays(-QuietDaysToClose).ToUnixTimeMilliseconds();
        foreach (var quiet in await db.LogIssues.Where(issue => issue.Status == LogIssueStatuses.Open && issue.LastSeenUnixMs < quietSince).ToListAsync(cancellationToken))
        {
            quiet.Status = LogIssueStatuses.Resolved;
            quiet.StatusNote = $"Not seen for {QuietDaysToClose} days (last in release {quiet.LastRelease}); it reopens if it comes back.";
        }
        await db.SaveChangesAsync(cancellationToken);
        var ids = await db.LogIssues.Where(issue => issue.LastSeenUnixMs < cutoff).Select(issue => issue.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0) return 0;
        await db.LogOccurrences.Where(occurrence => ids.Contains(occurrence.IssueId)).ExecuteDeleteAsync(cancellationToken);
        return await db.LogIssues.Where(issue => ids.Contains(issue.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    private static string TitleOf(CapturedLogEvent captured)
    {
        var text = captured.ClientMessage ?? captured.Message;
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static LogIssueSummary Summary(LogIssue issue) => new(
        issue.Id, issue.Title, issue.Level, issue.Origin, issue.Source, issue.ExceptionType, issue.Count,
        DateTimeOffset.FromUnixTimeMilliseconds(issue.FirstSeenUnixMs), DateTimeOffset.FromUnixTimeMilliseconds(issue.LastSeenUnixMs),
        issue.FirstRelease, issue.LastRelease, issue.Status, issue.StatusNote);

    private static string? Cap(string? value, int maximum) =>
        value is null ? null : value.Length <= maximum ? value : value[..maximum];
}

// Reads a trace back out of the JSON logs (Serilog's rendered compact format: @t, @m, @l, @x,
// @tr, plus properties). Newest files first, at most three of them, so a lookup stays quick on
// production's 50 MB files; the trace id is matched as text before a line is parsed.
public sealed class JsonLogTraceReader(string directory) : ILogTraceReader
{
    private const int FilesToSearch = 3;

    public async Task<IReadOnlyList<LogLine>> ReadTraceAsync(string traceId, int maximum = 500, CancellationToken cancellationToken = default)
    {
        traceId = traceId.Trim().ToLowerInvariant();
        if (traceId.Length != 32 || !traceId.All(Uri.IsHexDigit) || !Directory.Exists(directory)) return [];
        var lines = new List<LogLine>();
        var files = new DirectoryInfo(directory).GetFiles("*.clef").OrderByDescending(file => file.LastWriteTimeUtc).Take(FilesToSearch);
        foreach (var file in files)
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.Contains(traceId, StringComparison.Ordinal)) continue;
                if (Parse(line, traceId) is { } parsed) lines.Add(parsed);
                if (lines.Count >= maximum) break;
            }
            if (lines.Count >= maximum) break;
        }
        return lines.OrderBy(line => line.At).ToList();
    }

    public static LogLine? Parse(string json, string traceId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var matches = Text(root, "@tr") == traceId || Text(root, "ClientTraceId") == traceId;
            if (!matches || !DateTimeOffset.TryParse(Text(root, "@t"), out var at)) return null;
            return new LogLine(at, Text(root, "@l") ?? "Information", Text(root, "Origin") ?? "server", Text(root, "SourceContext") ?? "-",
                Text(root, "@m") ?? Text(root, "@mt") ?? string.Empty, Text(root, "@x"), Text(root, "UserName"),
                Text(root, "ClientPage") ?? Text(root, "RequestPath"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
