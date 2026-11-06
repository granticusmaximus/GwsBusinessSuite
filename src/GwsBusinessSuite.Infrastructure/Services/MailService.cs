using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;
using MailKit;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// IMailService: each person's own mailboxes (see MailModels). An account row says which provider
// and how to sign in; IMailboxBackend does the talking. Every call names the account, and only the
// person's own accounts are found. Provider and network failures come back as MailException with
// the server's reason, and the last one is kept on the account for the page.
public sealed class MailService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    IEnumerable<IMailboxBackend> backends,
    TimeProvider timeProvider) : GwsBusinessSuite.Application.Mail.IMailService
{
    private const int MaxSearchLength = 200;
    private const int MaxBodyLength = 1_000_000;
    private const int MaxRecipients = 100;

    public async Task<IReadOnlyList<MailAccountView>> ListAccountsAsync(string username, CancellationToken cancellationToken = default) =>
        (await db.MailAccounts.AsNoTracking().Where(account => account.Username == username).ToListAsync(cancellationToken))
            .OrderBy(account => account.CreatedAt).Select(ToView).ToList();

    public async Task<MailAccountView> SaveImapAccountAsync(string username, Guid? accountId, MailImapAccountInput input, CancellationToken cancellationToken = default)
    {
        var address = (input.Address ?? string.Empty).Trim();
        if (!MailboxAddress.TryParse(address, out var parsed) || !parsed.Address.Contains('@'))
            throw new MailException("Enter the mailbox's email address.");
        var imapHost = Host(input.ImapHost, "IMAP server (e.g. imap.mail.me.com)");
        var smtpHost = Host(input.SmtpHost, "SMTP server (e.g. smtp.mail.me.com)");
        if (input.ImapPort is < 1 or > 65535 || input.SmtpPort is < 1 or > 65535) throw new MailException("Ports are between 1 and 65535.");
        if (!MailServerSecurity.All.Contains(input.SmtpSecurity)) throw new MailException("Choose how to secure the SMTP connection.");
        var login = string.IsNullOrWhiteSpace(input.LoginUsername) ? parsed.Address : input.LoginUsername.Trim();

        var account = accountId is { } id ? await FindAsync(username, id, cancellationToken) : null;
        if (account is null) await RequireRoomAsync(username, cancellationToken);
        var password = !string.IsNullOrEmpty(input.Password) ? input.Password.Replace(" ", string.Empty)
            : account is { Provider: MailProviders.Imap, PasswordProtected.Length: > 0 } ? secretProtector.Unprotect(account.PasswordProtected)
            : throw new MailException("Enter the mailbox's app password.");

        var candidate = new MailAccount
        {
            Username = username, Provider = MailProviders.Imap, Address = parsed.Address, DisplayName = (input.DisplayName ?? string.Empty).Trim(),
            ImapHost = imapHost, ImapPort = input.ImapPort, SmtpHost = smtpHost, SmtpPort = input.SmtpPort, SmtpSecurity = input.SmtpSecurity,
            LoginUsername = login
        };
        var warning = await Backend(MailProviders.Imap).VerifyAsync(candidate, password, cancellationToken);

        if (account is null)
        {
            account = new MailAccount { Username = username, CreatedBy = username };
            db.MailAccounts.Add(account);
        }
        else if (account.Provider == MailProviders.Google)
        {
            await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        }
        account.Provider = MailProviders.Imap;
        account.Address = candidate.Address;
        account.DisplayName = candidate.DisplayName;
        account.CredentialId = null;
        account.TokenProtected = string.Empty;
        account.ImapHost = imapHost;
        account.ImapPort = input.ImapPort;
        account.SmtpHost = smtpHost;
        account.SmtpPort = input.SmtpPort;
        account.SmtpSecurity = input.SmtpSecurity;
        account.LoginUsername = login;
        account.PasswordProtected = secretProtector.Protect(password);
        account.LastError = null;
        account.UpdatedAt = timeProvider.GetUtcNow();
        account.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account) with { Warning = warning };
    }

    public async Task<MailAccountView> SaveGoogleAccountAsync(string username, string tokenJson, string address, CancellationToken cancellationToken = default)
    {
        var trimmed = address.Trim();
        var account = (await db.MailAccounts.Where(item => item.Username == username).ToListAsync(cancellationToken))
            .FirstOrDefault(item => item.Provider == MailProviders.Google && string.Equals(item.Address, trimmed, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            await RequireRoomAsync(username, cancellationToken);
            account = new MailAccount { Username = username, CreatedBy = username };
            db.MailAccounts.Add(account);
        }
        await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        account.Provider = MailProviders.Google;
        account.Address = trimmed;
        account.CredentialId = null;
        account.TokenProtected = secretProtector.Protect(tokenJson);
        account.ImapHost = account.SmtpHost = account.LoginUsername = account.PasswordProtected = string.Empty;
        account.LastError = null;
        account.UpdatedAt = timeProvider.GetUtcNow();
        account.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account);
    }

    public async Task RemoveAccountAsync(string username, Guid accountId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is not { } account) return;
        await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(string username, Guid accountId, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, (backend, account) => backend.ListFoldersAsync(account, cancellationToken), cancellationToken);

    public Task<MailPage> ListAsync(string username, Guid accountId, string folder, string? search, string? pageToken, CancellationToken cancellationToken = default)
    {
        if (!MailFolders.IsKnown(folder)) throw new MailException("Unknown folder.");
        var query = search?.Trim();
        if (query is { Length: > MaxSearchLength }) query = query[..MaxSearchLength];
        return RunAsync(username, accountId, (backend, account) => backend.ListAsync(account, folder, query, pageToken, cancellationToken), cancellationToken);
    }

    public Task<MailMessageDetail> GetAsync(string username, Guid accountId, string messageId, bool showRemoteImages, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var fetched = await backend.GetAsync(account, messageId, markRead: true, cancellationToken);
            return MailMessageRenderer.Render(messageId, fetched.ThreadId, fetched.Message, fetched.Unread, fetched.Starred, showRemoteImages);
        }, cancellationToken);

    public Task<MailAttachmentContent> GetAttachmentAsync(string username, Guid accountId, string messageId, int index, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var message = (await backend.GetAsync(account, messageId, markRead: false, cancellationToken)).Message;
            var attachments = MailMessageRenderer.Attachments(message);
            if (index < 0 || index >= attachments.Count) throw new MailException("That attachment isn't there.");
            var part = attachments[index];
            using var stream = new MemoryStream();
            if (part.Content is not null) await part.Content.DecodeToAsync(stream, cancellationToken);
            return new MailAttachmentContent(MailMessageRenderer.FileName(part, index), part.ContentType.MimeType, stream.ToArray());
        }, cancellationToken);

    public Task SendAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var (message, threadId) = await BuildAsync(backend, account, draft, sending: true, cancellationToken);
            await backend.SendAsync(account, message, draft.Forward ? null : threadId, cancellationToken);
            if (draft.DraftId is { } draftId)
            {
                try
                {
                    await backend.DeleteDraftAsync(account, draftId, cancellationToken);
                }
                catch (MailException)
                {
                    throw new MailException("Sent - but its draft is still in Drafts; delete it there.");
                }
            }
            return true;
        }, cancellationToken);

    public Task<string> SaveDraftAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var (message, threadId) = await BuildAsync(backend, account, draft, sending: false, cancellationToken);
            return await backend.SaveDraftAsync(account, message, draft.DraftId, draft.Forward ? null : threadId, cancellationToken);
        }, cancellationToken);

    public Task SetReadAsync(string username, Guid accountId, string messageId, bool read, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, (backend, account) => backend.SetReadAsync(account, messageId, read, cancellationToken), cancellationToken);

    public Task SetStarredAsync(string username, Guid accountId, string messageId, bool starred, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, (backend, account) => backend.SetStarredAsync(account, messageId, starred, cancellationToken), cancellationToken);

    public Task ArchiveAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, (backend, account) => backend.ArchiveAsync(account, messageId, cancellationToken), cancellationToken);

    public Task TrashAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, (backend, account) => backend.TrashAsync(account, messageId, cancellationToken), cancellationToken);

    // Each inbox counted on its own; one that can't be reached is left out rather than failing the rest.
    public Task<MailActionResult> ApplyAsync(string username, Guid accountId, IReadOnlyList<string> messageIds, string action,
        CancellationToken cancellationToken = default)
    {
        if (!MailActions.All.Contains(action)) throw new MailException("That isn't something Mail can do.");
        var ids = messageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return Task.FromResult(new MailActionResult(0, null));
        if (ids.Count > MailLimits.MaxBulk) throw new MailException($"Mail can act on up to {MailLimits.MaxBulk} messages at a time.");
        return RunAsync(username, accountId, async (backend, account) =>
            new MailActionResult(ids.Count, await backend.ApplyAsync(account, ids, action, cancellationToken)), cancellationToken);
    }

    public Task UndoAsync(string username, Guid accountId, MailUndo undo, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            await backend.UndoAsync(account, undo, cancellationToken);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> GetUnreadByAccountAsync(string username, CancellationToken cancellationToken = default)
    {
        var ids = await db.MailAccounts.AsNoTracking().Where(account => account.Username == username).Select(account => account.Id).ToListAsync(cancellationToken);
        var counts = new Dictionary<Guid, int>();
        foreach (var id in ids)
        {
            try
            {
                counts[id] = await RunAsync(username, id, (backend, account) => backend.UnreadCountAsync(account, cancellationToken), cancellationToken);
            }
            catch (MailException)
            {
                // That mailbox's own page says what's wrong.
            }
        }
        return counts;
    }

    public async Task<int?> GetUnreadCountAsync(string username, CancellationToken cancellationToken = default)
    {
        var ids = await db.MailAccounts.AsNoTracking().Where(account => account.Username == username).Select(account => account.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0) return null;
        var total = 0;
        foreach (var id in ids)
        {
            try
            {
                total += await RunAsync(username, id, (backend, account) => backend.UnreadCountAsync(account, cancellationToken), cancellationToken);
            }
            catch (MailException)
            {
                // That mailbox's own page says what's wrong.
            }
        }
        return total;
    }

    // The message to send or keep as a draft. A reply threads on the original; editing a draft keeps
    // the threading the draft already had; a forward carries the original's attachments.
    private async Task<(MimeMessage Message, string? ThreadId)> BuildAsync(IMailboxBackend backend, MailAccount account, MailDraft draft, bool sending, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName, account.Address));
        AddRecipients(message.To, draft.To);
        AddRecipients(message.Cc, draft.Cc);
        AddRecipients(message.Bcc, draft.Bcc);
        var recipients = message.To.Count + message.Cc.Count + message.Bcc.Count;
        if (sending && recipients == 0) throw new MailException("Add who it's to.");
        if (recipients > MaxRecipients) throw new MailException($"At most {MaxRecipients} recipients.");
        if (draft.Body.Length > MaxBodyLength) throw new MailException("That message is too long.");
        var uploads = draft.Attachments ?? [];
        if (uploads.Count > MailLimits.MaxAttachments) throw new MailException($"At most {MailLimits.MaxAttachments} attachments.");
        if (uploads.Sum(file => (long)file.Content.Length) > MailLimits.MaxAttachmentBytes)
            throw new MailException($"Attachments can add up to {MailLimits.MaxAttachmentBytes / (1024 * 1024)} MB.");
        message.Subject = draft.Subject.Trim();
        MimeEntity body = new MultipartAlternative
        {
            new TextPart("plain") { Text = draft.Body },
            new TextPart("html") { Text = MailFormatting.ToHtml(draft.Body) }
        };
        var attachments = uploads.Select(Attachment).ToList();

        string? threadId = null;
        var threadFrom = draft.ReplyToMessageId ?? draft.DraftId;
        if (!string.IsNullOrEmpty(threadFrom))
        {
            var original = await backend.GetAsync(account, threadFrom, markRead: false, cancellationToken);
            threadId = original.ThreadId;
            if (draft.ReplyToMessageId is null)
            {
                // Editing a draft: the same threading headers, and the files it already had.
                message.InReplyTo = original.Message.InReplyTo;
                foreach (var reference in original.Message.References) message.References.Add(reference);
                attachments.InsertRange(0, MailMessageRenderer.Attachments(original.Message));
            }
            else if (!draft.Forward && original.Message.MessageId is { } replyingTo)
            {
                message.InReplyTo = replyingTo;
                foreach (var reference in original.Message.References) message.References.Add(reference);
                message.References.Add(replyingTo);
            }
            if (draft.Forward && draft.ReplyToMessageId is not null) attachments.InsertRange(0, MailMessageRenderer.Attachments(original.Message));
        }
        if (attachments.Count > 0)
        {
            var mixed = new Multipart("mixed") { body };
            foreach (var attachment in attachments) mixed.Add(attachment);
            body = mixed;
        }
        message.Body = body;
        return (message, threadId);
    }

    private Task RunAsync(string username, Guid accountId, Func<IMailboxBackend, MailAccount, Task> work, CancellationToken cancellationToken) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            await work(backend, account);
            return true;
        }, cancellationToken);

    // Runs one mailbox operation; the account's LastError follows the outcome (written only when it changes).
    private async Task<T> RunAsync<T>(string username, Guid accountId, Func<IMailboxBackend, MailAccount, Task<T>> work, CancellationToken cancellationToken)
    {
        var account = await FindAsync(username, accountId, cancellationToken) ?? throw new MailException("Connect a mailbox first.");
        await MoveFirstVersionSignInAsync(account, cancellationToken);
        var signInBefore = account.TokenProtected;
        string? failure = null;
        try
        {
            return await work(Backend(account.Provider), account);
        }
        catch (MailException ex)
        {
            failure = ex.Message;
            throw;
        }
        catch (Exception ex) when (ex is ServiceNotConnectedException or ServiceNotAuthenticatedException or ProtocolException or CommandException
                                       or AuthenticationException or SslHandshakeException or FolderNotFoundException or MessageNotFoundException
                                       or IOException or HttpRequestException or System.Net.Sockets.SocketException or TimeoutException
                                       or System.Text.Json.JsonException)
        {
            failure = $"The mail server said: {ex.Message}";
            throw new MailException(failure);
        }
        finally
        {
            var changed = account.TokenProtected != signInBefore; // a renewed Google sign-in
            if (account.LastError != failure && failure is not "That message is gone.")
            {
                account.LastError = failure;
                if (failure is null) account.LastUsedAt = timeProvider.GetUtcNow();
                changed = true;
            }
            if (changed) await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // Only the person's own: someone else's account id finds nothing.
    private Task<MailAccount?> FindAsync(string username, Guid accountId, CancellationToken cancellationToken) =>
        db.MailAccounts.FirstOrDefaultAsync(item => item.Id == accountId && item.Username == username, cancellationToken);

    private async Task RequireRoomAsync(string username, CancellationToken cancellationToken)
    {
        if (await db.MailAccounts.CountAsync(account => account.Username == username, cancellationToken) >= MailLimits.MaxAccounts)
            throw new MailException($"You can connect up to {MailLimits.MaxAccounts} mailboxes - remove one first.");
    }

    private IMailboxBackend Backend(string provider) =>
        backends.FirstOrDefault(backend => backend.Provider == provider) ?? throw new MailException($"Mail can't use '{provider}' accounts.");

    private static void AddRecipients(InternetAddressList list, IReadOnlyList<string> entries)
    {
        foreach (var entry in entries.SelectMany(entry => entry.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!MailboxAddress.TryParse(entry, out var address) || !address.Address.Contains('@'))
                throw new MailException($"'{entry}' isn't an email address.");
            list.Add(address);
        }
    }

    private static MimePart Attachment(MailAttachmentUpload file)
    {
        var name = Path.GetFileName(file.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        if (!ContentType.TryParse(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, out var type))
            type = new ContentType("application", "octet-stream");
        return new MimePart(type)
        {
            Content = new MimeContent(new MemoryStream(file.Content)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = name
        };
    }

    private static string Host(string? value, string what)
    {
        var host = (value ?? string.Empty).Trim();
        if (host.Length is 0 or > 200 || host.Contains(' ') || host.Contains('/')) throw new MailException($"Enter the {what}.");
        return host;
    }

    // The first version (31-10-26) kept a Gmail sign-in as the Automation credential "Mail -
    // <username>", where every admin could use it. It moves onto the account, and the credential goes.
    private async Task MoveFirstVersionSignInAsync(MailAccount account, CancellationToken cancellationToken)
    {
        if (account is not { Provider: MailProviders.Google, TokenProtected.Length: 0, CredentialId: { } credentialId }) return;
        if (await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == credentialId, cancellationToken) is { } credential)
        {
            account.TokenProtected = secretProtector.Protect(secretProtector.Unprotect(credential.ProtectedData));
            db.AutomationCredentials.Remove(credential);
        }
        account.CredentialId = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveCredentialAsync(Guid? credentialId, CancellationToken cancellationToken)
    {
        if (credentialId is not { } id) return;
        if (await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is { } credential)
            db.AutomationCredentials.Remove(credential);
    }

    private static MailAccountView ToView(MailAccount account) =>
        new(account.Id, account.Provider, account.Address, account.DisplayName, account.LastError, account.LastUsedAt);
}
