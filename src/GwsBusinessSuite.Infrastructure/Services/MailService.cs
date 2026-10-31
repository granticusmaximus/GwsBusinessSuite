using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;
using MailKit;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// IMailService: each person's own mailbox (see MailModels). The account row says which provider
// and how to sign in; IMailboxBackend does the talking. Provider and network failures come back as
// MailException with the server's reason, and the last one is kept on the account for the page.
public sealed class MailService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    IEnumerable<IMailboxBackend> backends,
    TimeProvider timeProvider) : GwsBusinessSuite.Application.Mail.IMailService
{
    private const int MaxSearchLength = 200;
    private const int MaxBodyLength = 1_000_000;
    private const int MaxRecipients = 100;

    public async Task<MailAccountView?> GetAccountAsync(string username, CancellationToken cancellationToken = default) =>
        await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(account => account.Username == username, cancellationToken) is { } account
            ? ToView(account)
            : null;

    public async Task<MailAccountView> SaveImapAccountAsync(string username, MailImapAccountInput input, CancellationToken cancellationToken = default)
    {
        var address = (input.Address ?? string.Empty).Trim();
        if (!MailboxAddress.TryParse(address, out var parsed) || !parsed.Address.Contains('@'))
            throw new MailException("Enter the mailbox's email address.");
        var imapHost = Host(input.ImapHost, "IMAP server (e.g. imap.mail.me.com)");
        var smtpHost = Host(input.SmtpHost, "SMTP server (e.g. smtp.mail.me.com)");
        if (input.ImapPort is < 1 or > 65535 || input.SmtpPort is < 1 or > 65535) throw new MailException("Ports are between 1 and 65535.");
        if (!MailServerSecurity.All.Contains(input.SmtpSecurity)) throw new MailException("Choose how to secure the SMTP connection.");
        var login = string.IsNullOrWhiteSpace(input.LoginUsername) ? parsed.Address : input.LoginUsername.Trim();

        var account = await db.MailAccounts.FirstOrDefaultAsync(item => item.Username == username, cancellationToken);
        var password = !string.IsNullOrEmpty(input.Password) ? input.Password.Replace(" ", string.Empty)
            : account is { Provider: MailProviders.Imap, PasswordProtected.Length: > 0 } ? secretProtector.Unprotect(account.PasswordProtected)
            : throw new MailException("Enter the mailbox's app password.");

        var candidate = new MailAccount
        {
            Username = username, Provider = MailProviders.Imap, Address = parsed.Address, DisplayName = (input.DisplayName ?? string.Empty).Trim(),
            ImapHost = imapHost, ImapPort = input.ImapPort, SmtpHost = smtpHost, SmtpPort = input.SmtpPort, SmtpSecurity = input.SmtpSecurity,
            LoginUsername = login
        };
        await Backend(MailProviders.Imap).VerifyAsync(candidate, password, cancellationToken);

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
        return ToView(account);
    }

    public async Task<MailAccountView> SaveGoogleAccountAsync(string username, Guid credentialId, string address, CancellationToken cancellationToken = default)
    {
        var account = await db.MailAccounts.FirstOrDefaultAsync(item => item.Username == username, cancellationToken);
        if (account is null)
        {
            account = new MailAccount { Username = username, CreatedBy = username };
            db.MailAccounts.Add(account);
        }
        account.Provider = MailProviders.Google;
        account.Address = address.Trim();
        account.CredentialId = credentialId;
        account.ImapHost = account.SmtpHost = account.LoginUsername = account.PasswordProtected = string.Empty;
        account.LastError = null;
        account.UpdatedAt = timeProvider.GetUtcNow();
        account.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account);
    }

    public async Task RemoveAccountAsync(string username, CancellationToken cancellationToken = default)
    {
        if (await db.MailAccounts.FirstOrDefaultAsync(item => item.Username == username, cancellationToken) is not { } account) return;
        await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<MailPage> ListAsync(string username, string folder, string? search, string? pageToken, CancellationToken cancellationToken = default)
    {
        if (!MailFolders.IsKnown(folder)) throw new MailException("Unknown folder.");
        var query = search?.Trim();
        if (query is { Length: > MaxSearchLength }) query = query[..MaxSearchLength];
        return RunAsync(username, (backend, account) => backend.ListAsync(account, folder, query, pageToken, cancellationToken), cancellationToken);
    }

    public Task<MailMessageDetail> GetAsync(string username, string messageId, bool showRemoteImages, CancellationToken cancellationToken = default) =>
        RunAsync(username, async (backend, account) =>
        {
            var fetched = await backend.GetAsync(account, messageId, markRead: true, cancellationToken);
            return MailMessageRenderer.Render(messageId, fetched.ThreadId, fetched.Message, fetched.Unread, fetched.Starred, showRemoteImages);
        }, cancellationToken);

    public Task<MailAttachmentContent> GetAttachmentAsync(string username, string messageId, int index, CancellationToken cancellationToken = default) =>
        RunAsync(username, async (backend, account) =>
        {
            var message = (await backend.GetAsync(account, messageId, markRead: false, cancellationToken)).Message;
            var attachments = MailMessageRenderer.Attachments(message);
            if (index < 0 || index >= attachments.Count) throw new MailException("That attachment isn't there.");
            var part = attachments[index];
            using var stream = new MemoryStream();
            if (part.Content is not null) await part.Content.DecodeToAsync(stream, cancellationToken);
            return new MailAttachmentContent(MailMessageRenderer.FileName(part, index), part.ContentType.MimeType, stream.ToArray());
        }, cancellationToken);

    public Task SendAsync(string username, MailDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(username, async (backend, account) =>
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(account.DisplayName, account.Address));
            AddRecipients(message.To, draft.To);
            AddRecipients(message.Cc, draft.Cc);
            AddRecipients(message.Bcc, draft.Bcc);
            if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0) throw new MailException("Add who it's to.");
            if (message.To.Count + message.Cc.Count + message.Bcc.Count > MaxRecipients) throw new MailException($"At most {MaxRecipients} recipients.");
            if (draft.Body.Length > MaxBodyLength) throw new MailException("That message is too long.");
            message.Subject = draft.Subject.Trim();
            var body = new TextPart("plain") { Text = draft.Body };

            string? threadId = null;
            if (!string.IsNullOrEmpty(draft.ReplyToMessageId))
            {
                var original = await backend.GetAsync(account, draft.ReplyToMessageId, markRead: false, cancellationToken);
                threadId = original.ThreadId;
                if (!draft.Forward && original.Message.MessageId is { } replyingTo)
                {
                    message.InReplyTo = replyingTo;
                    foreach (var reference in original.Message.References) message.References.Add(reference);
                    message.References.Add(replyingTo);
                }
                if (draft.Forward && MailMessageRenderer.Attachments(original.Message) is { Count: > 0 } attachments)
                {
                    var mixed = new Multipart("mixed") { body };
                    foreach (var attachment in attachments) mixed.Add(attachment);
                    message.Body = mixed;
                }
            }
            message.Body ??= body;
            await backend.SendAsync(account, message, draft.Forward ? null : threadId, cancellationToken);
            return true;
        }, cancellationToken);

    public Task SetReadAsync(string username, string messageId, bool read, CancellationToken cancellationToken = default) =>
        RunAsync(username, (backend, account) => backend.SetReadAsync(account, messageId, read, cancellationToken), cancellationToken);

    public Task SetStarredAsync(string username, string messageId, bool starred, CancellationToken cancellationToken = default) =>
        RunAsync(username, (backend, account) => backend.SetStarredAsync(account, messageId, starred, cancellationToken), cancellationToken);

    public Task ArchiveAsync(string username, string messageId, CancellationToken cancellationToken = default) =>
        RunAsync(username, (backend, account) => backend.ArchiveAsync(account, messageId, cancellationToken), cancellationToken);

    public Task TrashAsync(string username, string messageId, CancellationToken cancellationToken = default) =>
        RunAsync(username, (backend, account) => backend.TrashAsync(account, messageId, cancellationToken), cancellationToken);

    private Task RunAsync(string username, Func<IMailboxBackend, MailAccount, Task> work, CancellationToken cancellationToken) =>
        RunAsync(username, async (backend, account) =>
        {
            await work(backend, account);
            return true;
        }, cancellationToken);

    // Runs one mailbox operation; the account's LastError follows the outcome (written only when it changes).
    private async Task<T> RunAsync<T>(string username, Func<IMailboxBackend, MailAccount, Task<T>> work, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.FirstOrDefaultAsync(item => item.Username == username, cancellationToken)
            ?? throw new MailException("Connect a mailbox first.");
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
            if (account.LastError != failure && failure is not "That message is gone.")
            {
                account.LastError = failure;
                if (failure is null) account.LastUsedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
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

    private static string Host(string? value, string what)
    {
        var host = (value ?? string.Empty).Trim();
        if (host.Length is 0 or > 200 || host.Contains(' ') || host.Contains('/')) throw new MailException($"Enter the {what}.");
        return host;
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
