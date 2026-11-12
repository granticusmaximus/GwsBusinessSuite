using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Diagnostics;

// A warning or error as the issues store sees it (logging plan, Phase 1, 12-11-26), taken from a
// Serilog event in the Web project. ClientMessage is set for browser and Mac app reports, whose
// template is shared, so they group by what actually went wrong.
public sealed record CapturedLogEvent(
    DateTimeOffset At,
    string Level,
    string Origin,
    string Source,
    string MessageTemplate,
    string Message,
    string? ExceptionType,
    string? Exception,
    string? TraceId,
    string? UserName,
    string? Page,
    string? CircuitId,
    string Release,
    string? ClientMessage = null,
    string? ClientStack = null);

public static partial class LogFingerprint
{
    // Same cause, same fingerprint: the source, the message's shape (numbers, ids and quoted
    // values blanked), the exception type and the first frame in our own code. A message that
    // only differs by a record id or a count is the same issue.
    public static string For(CapturedLogEvent captured)
    {
        var shape = captured.ClientMessage is { } client
            ? Normalize(client) + "|" + Normalize(FirstLine(captured.ClientStack))
            : Normalize(captured.MessageTemplate);
        var key = string.Join('|', captured.Origin, captured.Source, shape, captured.ExceptionType ?? string.Empty, FirstAppFrame(captured.Exception) ?? string.Empty);
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = Guid().Replace(text, "{id}");
        text = Hex().Replace(text, "{hex}");
        text = Quoted().Replace(text, "\"…\"");
        text = Url().Replace(text, "{url}");
        text = Number().Replace(text, "#");
        return text.Trim();
    }

    // "at GwsBusinessSuite.Infrastructure.Services.MailService.SendAsync(...) in /src/...:line 42"
    // -> "GwsBusinessSuite.Infrastructure.Services.MailService.SendAsync", without line numbers,
    // which move with every unrelated edit.
    public static string? FirstAppFrame(string? exception)
    {
        if (string.IsNullOrEmpty(exception)) return null;
        foreach (var raw in exception.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("at GwsBusinessSuite.", StringComparison.Ordinal)) continue;
            var open = line.IndexOf('(');
            return open > 3 ? line[3..open] : line[3..];
        }
        return null;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var end = text.IndexOf('\n');
        return LineNumbers().Replace(end < 0 ? text : text[..end], string.Empty);
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b[0-9a-fA-F]{12,}\b")]
    private static partial Regex Hex();

    [GeneratedRegex("\"[^\"]*\"|'[^']*'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@":\d+(:\d+)?\)?$")]
    private static partial Regex LineNumbers();
}

public sealed record LogIssueQuery(string? Origin = null, string? Status = null, string? Search = null, string? Release = null, int Take = 200);

public sealed record LogIssueSummary(
    Guid Id, string Title, string Level, string Origin, string Source, string? ExceptionType, int Count,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string FirstRelease, string LastRelease, string Status, string? StatusNote);

public sealed record LogOccurrenceView(DateTimeOffset At, string Message, string? TraceId, string? UserName, string? Page, string? CircuitId, string Release);

public sealed record LogIssueDetail(LogIssueSummary Issue, string? SampleStack, IReadOnlyList<LogOccurrenceView> Occurrences);

// One line from the JSON log, for a trace's timeline.
public sealed record LogLine(DateTimeOffset At, string Level, string Origin, string Source, string Message, string? Exception, string? UserName, string? Page);

public interface ILogIssueService
{
    Task RecordAsync(IReadOnlyList<CapturedLogEvent> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LogIssueSummary>> ListAsync(LogIssueQuery query, CancellationToken cancellationToken = default);
    Task<LogIssueDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SetStatusAsync(Guid id, string status, string? note, CancellationToken cancellationToken = default);
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

// Every event in the JSON log that carries a trace id: the server's lines for the request and
// any browser or Mac app report from the page it served.
public interface ILogTraceReader
{
    Task<IReadOnlyList<LogLine>> ReadTraceAsync(string traceId, int maximum = 500, CancellationToken cancellationToken = default);
}

// The evidence for one issue as Markdown, for "Copy for Claude" (and Phase 2's SentinelGPT).
// Already redacted again here: it leaves the app.
public static class LogIssueBundle
{
    public static string ToMarkdown(LogIssueDetail detail, IReadOnlyList<LogLine> trace, string? currentRelease)
    {
        var issue = detail.Issue;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {issue.Title}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Level: {issue.Level}, origin: {issue.Origin}, source: `{issue.Source}`");
        if (issue.ExceptionType is not null) text.AppendLine(CultureInfo.InvariantCulture, $"- Exception: `{issue.ExceptionType}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Seen {issue.Count} times, first {Format(issue.FirstSeen)} (release {issue.FirstRelease}), last {Format(issue.LastSeen)} (release {issue.LastRelease})");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Running release: {currentRelease ?? "unknown"}; status: {issue.Status}{(issue.StatusNote is null ? string.Empty : " (" + issue.StatusNote + ")")}");
        if (!string.IsNullOrWhiteSpace(detail.SampleStack))
        {
            text.AppendLine().AppendLine("## Stack").AppendLine().AppendLine("```").AppendLine(detail.SampleStack.Trim()).AppendLine("```");
        }
        text.AppendLine().AppendLine("## Latest occurrences").AppendLine();
        foreach (var occurrence in detail.Occurrences.Take(10))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- {Format(occurrence.At)} {occurrence.Page ?? "-"} user {occurrence.UserName ?? "-"} trace {occurrence.TraceId ?? "-"}: {occurrence.Message}");
        }
        if (trace.Count > 0)
        {
            text.AppendLine().AppendLine("## Trace of the latest occurrence").AppendLine().AppendLine("```");
            foreach (var line in trace.Take(200))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{line.At.ToUniversalTime():HH:mm:ss.fff} [{line.Level}] {line.Origin} {line.Source}: {line.Message}");
                if (line.Exception is not null) text.AppendLine(line.Exception.Trim());
            }
            text.AppendLine("```");
        }
        return LogRedactor.Redact(text.ToString())!;
    }

    private static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("dd-MM-yy HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
