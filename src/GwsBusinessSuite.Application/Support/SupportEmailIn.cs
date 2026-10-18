using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Support;

// Email-to-ticket. Mail sent to the support mailbox becomes a ticket: the sender is matched to
// (or added as) a CRM contact. A reply whose subject carries "[Ticket #xxxxxxxx]" and comes from
// that ticket's contact is added to the ticket instead. Staff replies are emailed with that tag
// and with Reply-To set to the mailbox, so the conversation can stay in email. Automatic mail
// (out-of-office, bounces, mailing lists) is skipped so two auto-responders can't loop.
public static partial class SupportEmailIn
{
    public const int MaxMessagesPerPoll = 25;
    public const int MaxBodyLength = 20_000;
    public const string SystemUser = "support-email-in";

    public static class Statuses
    {
        public const string Created = "created";
        public const string Replied = "replied";
        public const string Skipped = "skipped";
        public const string Failed = "failed";
    }

    // The tag put on outgoing ticket emails: the first 8 hex digits of the ticket id.
    public static string TicketReference(Guid ticketId) => ticketId.ToString("N")[..8];

    public static string SubjectTag(Guid ticketId) => $"[Ticket #{TicketReference(ticketId)}]";

    public static string? ParseTicketReference(string? subject) =>
        subject is not null && TicketTag().Match(subject) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;

    // The subject without the ticket tag and leading Re:/Fwd: prefixes.
    public static string CleanSubject(string? subject)
    {
        var text = TicketTag().Replace(subject ?? string.Empty, string.Empty);
        text = ReplyPrefix().Replace(text, string.Empty);
        return Whitespace().Replace(text, " ").Trim();
    }

    // Out-of-office replies, bounces and list mail (RFC 3834 Auto-Submitted, Precedence, and
    // the common vendor headers).
    public static bool IsAutomatic(IEnumerable<KeyValuePair<string, string>> headers, string? fromAddress)
    {
        foreach (var (name, rawValue) in headers)
        {
            var value = rawValue.Trim().ToLowerInvariant();
            var header = name.ToLowerInvariant();
            if (header == "auto-submitted" && value != "no") return true;
            if (header == "precedence" && value is "bulk" or "junk" or "list" or "auto_reply") return true;
            if (header is "x-autoreply" or "x-autorespond" or "list-id" or "list-unsubscribe") return true;
        }
        var local = (fromAddress ?? string.Empty).Split('@')[0].ToLowerInvariant();
        return local is "mailer-daemon" or "postmaster" or "noreply" or "no-reply" or "donotreply" or "do-not-reply";
    }

    // A reply's new text, without the quoted history below it ("On ... wrote:", "> " lines,
    // Outlook's "-----Original Message-----" / "From:" block).
    public static string StripQuotedReply(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith('>')) break;
            if (QuoteHeader().IsMatch(trimmed)) break;
            // "On Mon, 1 Jan 2026 at 10:00, Name <a@b.c>" wrapped onto two lines before "wrote:".
            if (trimmed.StartsWith("On ", StringComparison.Ordinal) && i + 1 < lines.Length
                && lines[i + 1].TrimEnd().EndsWith("wrote:", StringComparison.Ordinal)) break;
            if (trimmed is "-----Original Message-----" or "________________________________") break;
            if (trimmed.StartsWith("From:", StringComparison.Ordinal) && kept.Count > 0 && kept[^1].Trim().Length == 0) break;
            kept.Add(line);
        }
        return string.Join('\n', kept).Trim();
    }

    [GeneratedRegex(@"\[Ticket #([0-9a-fA-F]{8})\]")]
    private static partial Regex TicketTag();

    [GeneratedRegex(@"^\s*((re|fw|fwd|aw|sv)\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^On .+ wrote:$")]
    private static partial Regex QuoteHeader();
}

public sealed record SupportMailboxView(
    bool Enabled, string Host, int Port, string Username, bool HasPassword, string Folder, string AuthServId,
    long? LastPollUnix, long? LastSuccessUnix, string? LastError);

public sealed record SupportMailboxUpdate(
    bool Enabled, string? Host, int Port, string? Username, string? Password, string? Folder, string? AuthServId);

public sealed record SupportInboundEmailView(
    Guid Id, string FromAddress, string Subject, string Status, string? Reason, Guid? TicketId, long ProcessedAtUnix);

public sealed class SupportEmailInException(string message) : Exception(message);

public interface ISupportEmailInService
{
    Task<SupportMailboxView> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<SupportMailboxView> SaveSettingsAsync(SupportMailboxUpdate update, string performedBy, CancellationToken cancellationToken = default);
    Task<string> TestConnectionAsync(string performedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportInboundEmailView>> ListRecentAsync(int count, CancellationToken cancellationToken = default);
    // Checks the mailbox once; returns how many messages were handled.
    Task<int> PollAsync(CancellationToken cancellationToken = default);
    // The address clients should reply to (the mailbox), or null while email-in is off.
    Task<string?> GetReplyToAddressAsync(CancellationToken cancellationToken = default);
}
