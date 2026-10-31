namespace GwsBusinessSuite.Application.Mail;

// Mail (/admin/mail, 31-10-26): each person's own mailbox in the app, read live from it - Gmail
// through Google's API (the person's own Google connection) or any other provider over IMAP and
// SMTP with an app password. Nothing from the mailbox is stored in the app.
public static class MailProviders
{
    public const string Google = "google";
    public const string Imap = "imap";
}

public static class MailFolders
{
    public const string Inbox = "inbox";
    public const string Starred = "starred";
    public const string Sent = "sent";
    public const string Drafts = "drafts";
    public const string Archive = "archive";
    public const string Spam = "spam";
    public const string Trash = "trash";

    public static readonly IReadOnlyList<(string Key, string Label, string Icon)> All =
    [
        (Inbox, "Inbox", "bi-inbox"), (Starred, "Starred", "bi-star"), (Sent, "Sent", "bi-send"), (Drafts, "Drafts", "bi-file-earmark"),
        (Archive, "Archive", "bi-archive"), (Spam, "Spam", "bi-exclamation-octagon"), (Trash, "Trash", "bi-trash")
    ];

    public static bool IsKnown(string? folder) => All.Any(f => f.Key == folder);
}

public sealed record MailAccountView(Guid Id, string Provider, string Address, string DisplayName, string? LastError, DateTimeOffset? LastUsedAt);

// Other providers: the IMAP server mail is read from and the SMTP server it's sent through.
public sealed record MailImapAccountInput(
    string Address, string DisplayName, string ImapHost, int ImapPort, string SmtpHost, int SmtpPort, string SmtpSecurity,
    string LoginUsername, string? Password);

public sealed record MailAddress(string Name, string Address)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public sealed record MailMessageSummary(
    string Id, string? ThreadId, MailAddress From, IReadOnlyList<MailAddress> To, string Subject, string Snippet,
    DateTimeOffset Date, bool Unread, bool Starred, bool HasAttachments);

// NextPageToken: null at the end.
public sealed record MailPage(IReadOnlyList<MailMessageSummary> Messages, string? NextPageToken);

public sealed record MailAttachmentInfo(int Index, string FileName, string ContentType, long Size);

// HtmlDocument: the message as a sanitized HTML document for a sandboxed frame (no scripts;
// remote images left out unless asked for). BlockedRemoteImages: how many were left out.
public sealed record MailMessageDetail(
    string Id, string? ThreadId, MailAddress From, IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> ReplyTo, string Subject, DateTimeOffset Date, string HtmlDocument, string TextBody,
    int BlockedRemoteImages, IReadOnlyList<MailAttachmentInfo> Attachments, string? MessageIdHeader, IReadOnlyList<string> References,
    bool Unread, bool Starred);

public sealed record MailAttachmentContent(string FileName, string ContentType, byte[] Content);

// A file attached to a message being written.
public sealed record MailAttachmentUpload(string FileName, string ContentType, byte[] Content);

// A new message, or a reply/forward of ReplyToMessageId (its threading headers and, for a
// forward, its attachments are carried over). Body is Markdown: it's sent as plain text and as
// the HTML it formats to.
public sealed record MailDraft(
    IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc, string Subject, string Body,
    string? ReplyToMessageId = null, bool Forward = false, IReadOnlyList<MailAttachmentUpload>? Attachments = null);

public static class MailLimits
{
    // Gmail's own limit for what you attach (the encoded message may be a third bigger).
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;
    public const int MaxAttachments = 20;
}

public sealed class MailException(string message) : Exception(message);

public interface IMailService
{
    Task<MailAccountView?> GetAccountAsync(string username, CancellationToken cancellationToken = default);
    // Signs in to both servers before saving; throws MailException with what went wrong.
    Task<MailAccountView> SaveImapAccountAsync(string username, MailImapAccountInput input, CancellationToken cancellationToken = default);
    // tokenJson: the Google sign-in (GoogleOAuthService.CompleteMailAuthorizationAsync), kept protected on the account.
    Task<MailAccountView> SaveGoogleAccountAsync(string username, string tokenJson, string address, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string username, CancellationToken cancellationToken = default);

    Task<MailPage> ListAsync(string username, string folder, string? search, string? pageToken, CancellationToken cancellationToken = default);
    // Opening a message marks it read, as mail apps do.
    Task<MailMessageDetail> GetAsync(string username, string messageId, bool showRemoteImages, CancellationToken cancellationToken = default);
    Task<MailAttachmentContent> GetAttachmentAsync(string username, string messageId, int index, CancellationToken cancellationToken = default);
    Task SendAsync(string username, MailDraft draft, CancellationToken cancellationToken = default);
    Task SetReadAsync(string username, string messageId, bool read, CancellationToken cancellationToken = default);
    Task SetStarredAsync(string username, string messageId, bool starred, CancellationToken cancellationToken = default);
    Task ArchiveAsync(string username, string messageId, CancellationToken cancellationToken = default);
    Task TrashAsync(string username, string messageId, CancellationToken cancellationToken = default);
    // Unread messages in the inbox; null when the person has no mailbox connected.
    Task<int?> GetUnreadCountAsync(string username, CancellationToken cancellationToken = default);

}
