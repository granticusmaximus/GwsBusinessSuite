using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Email into Sentinel (item 29). Sentinel polls one mailbox over IMAP; a message from a known
// user (their member-profile email) becomes a page under "Inbox", with its attachments. The From
// header alone is easy to forge, so the receiving server's own verdict (Authentication-Results)
// must show the sender's domain passed DMARC, or DKIM signed for that domain.
public static partial class SentinelEmailIn
{
    public const string FolderSystemKey = "email-inbox";
    public const string SystemUser = "email-in";
    public const int MaxMessagesPerPoll = 20;
    public const int MaxAttachments = 10;
    public const long MaxAttachmentBytes = 10 * 1024 * 1024;
    public const long MaxTotalAttachmentBytes = 25 * 1024 * 1024;
    public const int MaxBodyLength = 200_000;

    public static class Statuses
    {
        public const string Imported = "imported";
        public const string Rejected = "rejected";
        public const string Failed = "failed";
    }

    // "Q3 plan #client/acme #todo" -> ("Q3 plan", ["client/acme", "todo"]).
    public static (string Title, IReadOnlyList<string> Tags) ParseSubject(string? subject)
    {
        var (tags, rest) = SentinelTags.SplitQuery(subject ?? string.Empty);
        var title = ReplyPrefix().Replace(rest, string.Empty).Trim();
        return (title, tags);
    }

    // True when the topmost Authentication-Results header written by authServId (the receiving
    // server - the only one whose verdict counts; anything further down could have been written
    // by the sender) says dmarc=pass for the From domain, or dkim=pass with a signature for it.
    public static bool IsAuthenticated(IEnumerable<string> authenticationResults, string authServId, string fromDomain)
    {
        if (string.IsNullOrWhiteSpace(fromDomain) || string.IsNullOrWhiteSpace(authServId)) return false;
        var header = authenticationResults.FirstOrDefault(value =>
            string.Equals(value.Split(';', 2)[0].Trim().Split(' ')[0], authServId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (header is null) return false;
        var domain = fromDomain.Trim().ToLowerInvariant();
        foreach (var result in header.Split(';').Skip(1).Select(part => part.Trim()))
        {
            var lower = result.ToLowerInvariant();
            if (lower.StartsWith("dmarc=pass", StringComparison.Ordinal)
                && Property(lower, "header.from") == domain) return true;
            if (lower.StartsWith("dkim=pass", StringComparison.Ordinal)
                && (Property(lower, "header.d") == domain
                    || Property(lower, "header.i") is { } identity && identity.TrimStart('@').Split('@')[^1] == domain)) return true;
        }
        return false;
    }

    private static string? Property(string result, string name)
    {
        var match = Regex.Match(result, $@"(?:^|\s){Regex.Escape(name)}=([^\s;]+)");
        return match.Success ? match.Groups[1].Value.Trim('"') : null;
    }

    [GeneratedRegex(@"^(?:(?:re|fw|fwd|aw)\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();
}

public sealed record SentinelMailboxView(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    bool HasPassword,
    string Folder,
    string AuthServId,
    long? LastPollUnix,
    long? LastSuccessUnix,
    string? LastError,
    // Set when no mailbox is saved here and the server's own mail account (the .env SMTP login)
    // is used instead - the plus-address to send notes to.
    string? ServerAccountAddress = null);

// Password null = keep the stored one.
public sealed record SentinelMailboxUpdate(bool Enabled, string Host, int Port, string Username, string? Password, string Folder, string AuthServId);

public sealed record SentinelInboundEmailView(
    Guid Id, string FromAddress, string Subject, string Status, string? Reason, string? Username, Guid? WikiPageId, int AttachmentCount, long ProcessedAtUnix);

public sealed class SentinelEmailInException(string message) : Exception(message);

public interface ISentinelEmailInService
{
    Task<SentinelMailboxView> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<SentinelMailboxView> SaveSettingsAsync(SentinelMailboxUpdate update, string performedBy, CancellationToken cancellationToken = default);
    // Turns email-in on or off while it runs on the server's own mail account.
    Task<SentinelMailboxView> SetServerAccountEnabledAsync(bool enabled, string performedBy, CancellationToken cancellationToken = default);
    // Connects and signs in with the saved settings; returns a short description or throws.
    Task<string> TestConnectionAsync(string performedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelInboundEmailView>> ListRecentAsync(int count, CancellationToken cancellationToken = default);
    // Fetches and imports unread mail when enabled; returns how many messages were handled.
    Task<int> PollAsync(CancellationToken cancellationToken = default);
}
