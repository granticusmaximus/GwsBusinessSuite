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

    // The provider's own folders and labels (31-10-26): "label:<Gmail label id>" or
    // "folder:<IMAP folder path>", from IMailService.ListFoldersAsync.
    public const string LabelPrefix = "label:";
    public const string FolderPrefix = "folder:";

    public static bool IsKnown(string? folder) =>
        All.Any(f => f.Key == folder) || MailTabs.Find(folder) is not null
        || folder is { Length: > 6 } && (folder.StartsWith(LabelPrefix, StringComparison.Ordinal) || folder.StartsWith(FolderPrefix, StringComparison.Ordinal));
}

// GWS's inbox tabs (21-11-26, Grant: "the Primary/Promotions/Social/Updates/Forums tabs ... I want a
// GWS version", sorted by GWS itself on every mailbox). MailSorter's rules place each message; the
// person's moves and SentinelGPT's decisions for unclear mail override them (MailMessageTab, MailSenderTab).
public static class MailTabs
{
    public sealed record Tab(string Key, string Label, string Icon, string Category);

    public static readonly IReadOnlyList<Tab> All =
    [
        new("inbox:primary", "Primary", "bi-inbox", "primary"),
        new("inbox:promotions", "Promotions", "bi-tag", "promotions"),
        new("inbox:social", "Social", "bi-people", "social"),
        new("inbox:updates", "Updates", "bi-info-circle", "updates"),
        new("inbox:forums", "Forums", "bi-chat-left-text", "forums"),
    ];

    public static Tab Primary => All[0];

    public static Tab? Find(string? key) => All.FirstOrDefault(tab => tab.Key == key);

    // The inbox itself or one of its tabs (the sidebar's Inbox stays highlighted on every tab).
    public static bool IsInbox(string? folder) => folder == MailFolders.Inbox || Find(folder) is not null;

    // How many of the newest inbox messages are sorted into tabs.
    public const int SortWindow = 400;
}

// An inbox message with what MailSorter needs. MessageKey: its Message-ID header, lowercased, or the
// provider's id when it has none.
public sealed record MailInboxItem(MailMessageSummary Summary, string MessageKey, IReadOnlyDictionary<string, string> Headers);

// A tab's unread count and the newest unread senders' names (Gmail's "3 new · Amazon, LinkedIn").
public sealed record MailTabView(string Key, int Unread, IReadOnlyList<string> Senders);

public sealed record MailSenderTabView(string Sender, string TabKey);

// Where a listed message sits and why (the tab's hover text), for "Move to tab".
public sealed record MailTabPlacement(string MessageId, string TabKey, string Source, string Reason);

// One of the provider's own folders or labels. Color: Gmail's label colour, where it has one.
public sealed record MailFolderView(string Key, string Label, string? Color = null);

// Warning: only on the view SaveImapAccountAsync returns - the mailbox connected for reading,
// but this server can't reach its SMTP port, so sending won't work until the port opens.
public sealed record MailAccountView(Guid Id, string Provider, string Address, string DisplayName, string? LastError, DateTimeOffset? LastUsedAt, string? Warning = null,
    bool HasInboxTabs = false);

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
    string Id, string? ThreadId, MailAddress From, IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc, IReadOnlyList<MailAddress> Bcc,
    IReadOnlyList<MailAddress> ReplyTo, string Subject, DateTimeOffset Date, string HtmlDocument, string TextBody,
    int BlockedRemoteImages, IReadOnlyList<MailAttachmentInfo> Attachments, string? MessageIdHeader, IReadOnlyList<string> References,
    bool Unread, bool Starred);

public sealed record MailAttachmentContent(string FileName, string ContentType, byte[] Content);

// A file attached to a message being written.
public sealed record MailAttachmentUpload(string FileName, string ContentType, byte[] Content);

// A new message, or a reply/forward of ReplyToMessageId (its threading headers and, for a
// forward, its attachments are carried over). Body is Markdown: it's sent as plain text and as
// the HTML it formats to. DraftId: the saved draft this was opened from - saving replaces it,
// sending removes it.
public sealed record MailDraft(
    IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc, string Subject, string Body,
    string? ReplyToMessageId = null, bool Forward = false, IReadOnlyList<MailAttachmentUpload>? Attachments = null,
    string? DraftId = null);

public static class MailLimits
{
    // Gmail's own limit for what you attach (the encoded message may be a third bigger).
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;
    public const int MaxAttachments = 20;
    public const int MaxAccounts = 10;
    // Messages one action can take (a selection of loaded messages).
    public const int MaxBulk = 500;
}

// What can be done to one or many messages at once (06-11-26: select, right-click, swipe).
public static class MailActions
{
    public const string MarkRead = "read";
    public const string MarkUnread = "unread";
    public const string Star = "star";
    public const string Unstar = "unstar";
    public const string Archive = "archive";
    public const string Trash = "trash";
    // In Trash: gone for good, no Undo.
    public const string DeleteForever = "delete-forever";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { MarkRead, MarkUnread, Star, Unstar, Archive, Trash, DeleteForever };

    // Archive and both deletes take messages out of the folder they're in.
    public static bool Removes(string action) => action is Archive or Trash or DeleteForever;
}

// How to put messages back after Archive or Trash: the action it undoes, the messages as they are
// now (an IMAP move gives them new ids in the folder they went to) and the folder they came from.
public sealed record MailUndo(string Action, IReadOnlyList<string> Ids, string? FromFolder);

// Undo: null when it can't be undone (Delete forever, or an IMAP server without UIDPLUS).
public sealed record MailActionResult(int Count, MailUndo? Undo);

public sealed class MailException(string message) : Exception(message);

// A person's mailboxes, each named by its account id (31-10-26: several per person).
public interface IMailService
{
    Task<IReadOnlyList<MailAccountView>> ListAccountsAsync(string username, CancellationToken cancellationToken = default);
    // Signs in to both servers before saving; accountId null adds a mailbox. Throws MailException.
    Task<MailAccountView> SaveImapAccountAsync(string username, Guid? accountId, MailImapAccountInput input, CancellationToken cancellationToken = default);
    // tokenJson: the Google sign-in (GoogleOAuthService.CompleteMailAuthorizationAsync), kept protected on
    // the account. Connecting the same Gmail address again updates that mailbox.
    Task<MailAccountView> SaveGoogleAccountAsync(string username, string tokenJson, string address, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string username, Guid accountId, CancellationToken cancellationToken = default);

    // The provider's folders and labels beyond the standard ones.
    Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(string username, Guid accountId, CancellationToken cancellationToken = default);
    // Each inbox tab's unread count and newest unread senders (MailTabs).
    Task<IReadOnlyList<MailTabView>> ListTabsAsync(string username, Guid accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailTabView>>([]);
    // Moves a message to a tab and remembers its sender for that tab (Undo: MoveToTabAsync back, rememberSender false).
    Task MoveToTabAsync(string username, Guid accountId, string messageId, string tabKey, bool rememberSender, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    // Folders (phase 5): make, rename and delete the mailbox's own folders (Gmail: labels), and move
    // messages into one (Undo: MailActionResult.Undo). Throw MailException.
    Task<MailFolderView> CreateFolderAsync(string username, Guid accountId, string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task RenameFolderAsync(string username, Guid accountId, string folderKey, string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task DeleteFolderAsync(string username, Guid accountId, string folderKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<MailActionResult> MoveToFolderAsync(string username, Guid accountId, IReadOnlyList<string> messageIds, string folderKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    // Mail rules (phase 6): the mailbox's rules in order, saving one (Id Guid.Empty adds it), deleting
    // one, and running them now (includeExisting: on what's already in the inbox, not only new mail).
    Task<IReadOnlyList<MailRuleView>> ListRulesAsync(string username, Guid accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailRuleView>>([]);
    Task<MailRuleView> SaveRuleAsync(string username, Guid accountId, MailRuleView rule, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task DeleteRuleAsync(string username, Guid accountId, Guid ruleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<int> RunRulesAsync(string username, Guid accountId, bool includeExisting, CancellationToken cancellationToken = default) => Task.FromResult(0);
    // Senders the person moved to a tab, and forgetting one.
    Task<IReadOnlyList<MailSenderTabView>> ListSenderTabsAsync(string username, Guid accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailSenderTabView>>([]);
    Task ForgetSenderTabAsync(string username, Guid accountId, string sender, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<MailPage> ListAsync(string username, Guid accountId, string folder, string? search, string? pageToken, CancellationToken cancellationToken = default);
    // Opening a message marks it read, as mail apps do.
    Task<MailMessageDetail> GetAsync(string username, Guid accountId, string messageId, bool showRemoteImages, CancellationToken cancellationToken = default);
    Task<MailAttachmentContent> GetAttachmentAsync(string username, Guid accountId, string messageId, int index, CancellationToken cancellationToken = default);
    Task SendAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default);
    // Saves (or replaces draft.DraftId) in Drafts; returns the saved draft's message id.
    Task<string> SaveDraftAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default);
    Task SetReadAsync(string username, Guid accountId, string messageId, bool read, CancellationToken cancellationToken = default);
    Task SetStarredAsync(string username, Guid accountId, string messageId, bool starred, CancellationToken cancellationToken = default);
    Task ArchiveAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default);
    Task TrashAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default);
    // One MailActions action on up to MailLimits.MaxBulk messages, all in the same folder. Throws MailException.
    Task<MailActionResult> ApplyAsync(string username, Guid accountId, IReadOnlyList<string> messageIds, string action, CancellationToken cancellationToken = default);
    // Puts back what an Archive or Trash moved (MailActionResult.Undo).
    Task UndoAsync(string username, Guid accountId, MailUndo undo, CancellationToken cancellationToken = default);
    // Unread messages across all the person's inboxes; null when no mailbox is connected.
    Task<int?> GetUnreadCountAsync(string username, CancellationToken cancellationToken = default);
    // Each mailbox's unread inbox count, for the account switcher (a mailbox that can't be read is left out).
    Task<IReadOnlyDictionary<Guid, int>> GetUnreadByAccountAsync(string username, CancellationToken cancellationToken = default);
}
