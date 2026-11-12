using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Diagnostics;

// An error a browser or the Mac app reports to POST /api/diagnostics/client (logging plan,
// Phase 1, 12-11-26). Everything here comes from the client, so it is trimmed, capped and
// redacted before it reaches the log.
public sealed record ClientErrorInput(string? Origin, string? Kind, string? Message, string? Stack, string? Page, string? Trace);

public sealed record ClientErrorReport(string Origin, string Kind, string Message, string? Stack, string? Page, string? Trace)
{
    public const int MaximumMessage = 1000;
    public const int MaximumStack = 4000;
    public const int MaximumPage = 300;

    private static readonly HashSet<string> Origins = new(StringComparer.Ordinal) { "browser", "mac-app" };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "error", "rejection", "native" };
    private static readonly Regex TraceId = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    // Null when there is nothing worth logging (no message) or the origin is unknown.
    public static ClientErrorReport? From(ClientErrorInput? input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Message)) return null;
        var origin = input.Origin?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Origins.Contains(origin)) return null;
        var kind = input.Kind?.Trim().ToLowerInvariant() ?? "error";
        if (!Kinds.Contains(kind)) kind = "error";
        var trace = input.Trace?.Trim().ToLowerInvariant();
        return new ClientErrorReport(
            origin,
            kind,
            Cap(LogRedactor.Redact(input.Message.Trim()), MaximumMessage)!,
            Cap(LogRedactor.Redact(input.Stack?.Trim()), MaximumStack),
            Cap(LogRedactor.Redact(PathOnly(input.Page?.Trim())), MaximumPage),
            trace is not null && TraceId.IsMatch(trace) ? trace : null);
    }

    // The page without its query string or fragment, which can carry tokens and search text.
    private static string? PathOnly(string? page)
    {
        if (string.IsNullOrEmpty(page)) return null;
        var cut = page.IndexOfAny(['?', '#']);
        return cut < 0 ? page : page[..cut];
    }

    private static string? Cap(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maximum ? value : value[..maximum] + "…";
}

// Strips personal data and secrets from text bound for the logs, and later for a model
// (Phase 2). Errs on the side of removing too much: a log line can lose detail, but must never
// carry an address, a token or a password.
public static partial class LogRedactor
{
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Email().Replace(text, "[email]");
        text = Bearer().Replace(text, "$1 [redacted]");
        text = SecretParameter().Replace(text, "$1=[redacted]");
        text = Jwt().Replace(text, "[token]");
        return text;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(access_token|refresh_token|id_token|token|api_?key|key|secret|password|pwd|code|signature|sig)=[^&\s""']+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();
}
