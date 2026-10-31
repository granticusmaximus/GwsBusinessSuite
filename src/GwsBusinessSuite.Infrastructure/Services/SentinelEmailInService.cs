using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Email into Sentinel (item 29). See SentinelEmailIn for the rules.
public sealed class SentinelEmailInService(
    IAppDbContext db,
    IWikiService wikiService,
    ISentinelAccessService accessService,
    ISecretProtector secretProtector,
    TimeProvider timeProvider,
    ILogger<SentinelEmailInService> logger,
    ServerMailAccount? serverAccount = null,
    IKanbanBoardService? boards = null,
    IWikiDatabaseService? databases = null) : ISentinelEmailInService
{
    private readonly List<SentinelImportedFile> _pendingFiles = [];
    private const string ServerAccountTag = "sentinel";

    // What the poller reads: a mailbox saved in the panel (with its app password) wins; otherwise
    // the server's own mail account on its plus-address (see ServerMailAccount), on until turned
    // off here. Same rules as email-to-ticket (SupportEmailInService).
    private sealed record Mailbox(string Host, int Port, string Username, string Password, string Folder, string AuthServId,
        string Address, bool OnlyAddressed, bool Enabled, Guid? OAuthCredentialId = null);

    private Mailbox? Effective(SentinelMailboxSettings? settings)
    {
        if (settings is { PasswordProtected.Length: > 0 })
            return new Mailbox(settings.Host, settings.Port, settings.Username, secretProtector.Unprotect(settings.PasswordProtected),
                settings.Folder, settings.AuthServId, settings.Username, false, settings.Enabled);
        if (serverAccount?.For(ServerAccountTag) is not { } server) return null;
        return new Mailbox(server.Host, server.Port, server.Username, server.Password, "INBOX", server.AuthServId, server.Address, true,
            settings?.Enabled ?? true, server.OAuthCredentialId);
    }

    public async Task<SentinelMailboxView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        ToView(await db.SentinelMailboxSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken));

    public async Task<SentinelMailboxView> SetServerAccountEnabledAsync(bool enabled, string performedBy, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(performedBy, cancellationToken);
        var settings = await db.SentinelMailboxSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is { PasswordProtected.Length: > 0 }) throw new SentinelEmailInException("A mailbox is saved here, so email-in uses that one.");
        if (serverAccount?.For(ServerAccountTag) is null) throw new SentinelEmailInException("The server's mail account isn't a Google account this can read.");
        if (settings is null)
        {
            settings = new SentinelMailboxSettings { CreatedBy = performedBy };
            db.SentinelMailboxSettings.Add(settings);
        }
        settings.Enabled = enabled;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(settings);
    }

    public async Task<SentinelMailboxView> SaveSettingsAsync(SentinelMailboxUpdate update, string performedBy, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(performedBy, cancellationToken);
        var host = (update.Host ?? string.Empty).Trim();
        var username = (update.Username ?? string.Empty).Trim();
        var folder = string.IsNullOrWhiteSpace(update.Folder) ? "INBOX" : update.Folder.Trim();
        if (host.Length is 0 or > 200 || host.Contains(' ') || host.Contains('/')) throw new SentinelEmailInException("Enter the IMAP server's host name, e.g. imap.gmail.com.");
        if (update.Port is < 1 or > 65535) throw new SentinelEmailInException("The port must be between 1 and 65535 (IMAP over TLS is 993).");
        if (username.Length is 0 or > 320) throw new SentinelEmailInException("Enter the mailbox's sign-in address.");
        if (string.IsNullOrWhiteSpace(update.AuthServId)) throw new SentinelEmailInException("Enter the receiving server's name (mx.google.com for Google Workspace).");

        var settings = await db.SentinelMailboxSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new SentinelMailboxSettings { CreatedBy = performedBy };
            db.SentinelMailboxSettings.Add(settings);
        }
        settings.Host = host;
        settings.Port = update.Port;
        settings.Username = username;
        settings.Folder = folder;
        settings.AuthServId = update.AuthServId.Trim();
        if (!string.IsNullOrEmpty(update.Password)) settings.PasswordProtected = secretProtector.Protect(update.Password.Replace(" ", string.Empty));
        if (update.Enabled && settings.PasswordProtected.Length == 0) throw new SentinelEmailInException("Enter the mailbox's app password before turning email-in on.");
        settings.Enabled = update.Enabled;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(settings);
    }

    public async Task<string> TestConnectionAsync(string performedBy, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(performedBy, cancellationToken);
        var mailbox = Effective(await db.SentinelMailboxSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken))
            ?? throw new SentinelEmailInException("Save the mailbox settings first.");
        try
        {
            using var client = await ConnectAsync(mailbox, cancellationToken);
            var folder = await client.GetFolderAsync(mailbox.Folder, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var unread = await folder.SearchAsync(mailbox.OnlyAddressed ? ServerMailAccount.UnreadFor(mailbox.Address) : SearchQuery.NotSeen, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return mailbox.OnlyAddressed
                ? $"Connected through the server's mail account: {unread.Count} unread message(s) to {mailbox.Address} waiting."
                : $"Connected to {mailbox.Folder}: {unread.Count} unread message(s) waiting.";
        }
        catch (Exception ex) when (ex is not SentinelEmailInException and not OperationCanceledException)
        {
            throw new SentinelEmailInException($"Couldn't connect: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SentinelInboundEmailView>> ListRecentAsync(int count, CancellationToken cancellationToken = default) =>
        await db.SentinelInboundEmails.AsNoTracking()
            .OrderByDescending(item => item.ProcessedAtUnix)
            .Take(Math.Clamp(count, 1, 200))
            .Select(item => new SentinelInboundEmailView(item.Id, item.FromAddress, item.Subject, item.Status, item.Reason, item.Username, item.WikiPageId, item.AttachmentCount, item.ProcessedAtUnix))
            .ToListAsync(cancellationToken);

    public async Task<int> PollAsync(CancellationToken cancellationToken = default) =>
        await PollInboxAsync(cancellationToken) + await PollBoardsAsync(cancellationToken);

    private async Task<int> PollInboxAsync(CancellationToken cancellationToken)
    {
        var settings = await db.SentinelMailboxSettings.FirstOrDefaultAsync(cancellationToken);
        if (Effective(settings) is not { Enabled: true } mailbox) return 0;
        if (settings is null)
        {
            // The server's account needs somewhere to keep its last poll and error.
            settings = new SentinelMailboxSettings { Enabled = true, CreatedBy = "system" };
            db.SentinelMailboxSettings.Add(settings);
        }
        settings.LastPollUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var handled = 0;
        try
        {
            using var client = await ConnectAsync(mailbox, cancellationToken);
            var folder = await client.GetFolderAsync(mailbox.Folder, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            var unread = await folder.SearchAsync(mailbox.OnlyAddressed ? ServerMailAccount.UnreadFor(mailbox.Address) : SearchQuery.NotSeen, cancellationToken);
            foreach (var uid in unread.Take(SentinelEmailIn.MaxMessagesPerPoll))
            {
                var message = await folder.GetMessageAsync(uid, cancellationToken);
                // Anything not sent to the plus-address is left exactly as it was (still unread).
                if (mailbox.OnlyAddressed && !ServerMailAccount.IsAddressedTo(message, mailbox.Address)) continue;
                await ImportAsync(message, mailbox.AuthServId, cancellationToken);
                // Marked read only after it's recorded, so a crash mid-message retries it and
                // the Message-ID log stops a double import.
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                handled++;
            }
            await client.DisconnectAsync(true, cancellationToken);
            settings.LastSuccessUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            settings.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sentinel email-in poll failed");
            settings.LastError = Truncate(ex.Message, 500);
        }
        await db.SaveChangesAsync(cancellationToken);
        return handled;
    }

    // One message -> one log row, and a page when it's accepted. Public for tests.
    public async Task<SentinelInboundEmail> ImportAsync(MimeMessage message, string authServId, CancellationToken cancellationToken = default)
    {
        var messageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? $"no-id:{message.Date.ToUnixTimeSeconds()}:{message.From}:{message.Subject}"
            : message.MessageId;
        messageId = Truncate(messageId, 500);
        var existing = await db.SentinelInboundEmails.FirstOrDefaultAsync(item => item.MessageId == messageId, cancellationToken);
        if (existing is not null) return existing;

        var from = message.From.Mailboxes.FirstOrDefault();
        var log = new SentinelInboundEmail
        {
            MessageId = messageId,
            FromAddress = Truncate(from?.Address ?? string.Empty, 320),
            Subject = Truncate(message.Subject ?? string.Empty, 300),
            Status = SentinelEmailIn.Statuses.Rejected,
            ProcessedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            CreatedBy = SentinelEmailIn.SystemUser
        };
        db.SentinelInboundEmails.Add(log);
        try
        {
            var username = from is null ? null : await FindUserAsync(from.Address, cancellationToken);
            if (from is null || message.From.Mailboxes.Count() != 1)
                log.Reason = "The message didn't have exactly one sender.";
            else if (username is null)
                log.Reason = "The sender isn't a known user (no active account has this email on its member profile).";
            else if (!SentinelEmailIn.IsAuthenticated(
                         message.Headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value),
                         authServId, from.Domain))
                log.Reason = "The mail server couldn't confirm the sender (no DMARC or DKIM pass for their domain), so it may be forged.";
            else
            {
                log.Username = username;
                var folder = await EnsureFolderAsync(cancellationToken);
                if (!await accessService.CanAccessAsync(folder.Id, false, username, SentinelAccessLevels.Edit, cancellationToken))
                {
                    log.Reason = "The sender doesn't have edit access to the Inbox folder.";
                }
                else
                {
                    var page = await CreatePageAsync(message, from, username, folder.Id, log, cancellationToken);
                    log.WikiPageId = page.Id;
                    log.Status = SentinelEmailIn.Statuses.Imported;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Importing email {MessageId} into Sentinel failed", messageId);
            log.Status = SentinelEmailIn.Statuses.Failed;
            log.Reason = Truncate(ex.Message, 500);
            // Files that failed to save would fail the log row's save too.
            foreach (var file in _pendingFiles) db.SentinelImportedFiles.Remove(file);
            _pendingFiles.Clear();
        }
        await db.SaveChangesAsync(cancellationToken);
        return log;
    }

    private async Task<WikiPage> CreatePageAsync(MimeMessage message, MailboxAddress from, string username, Guid folderId, SentinelInboundEmail log, CancellationToken cancellationToken)
    {
        var (subjectTitle, tags) = SentinelEmailIn.ParseSubject(message.Subject);
        var title = subjectTitle.Length > 0 ? Truncate(subjectTitle, 200) : $"Email from {from.Name ?? from.Address}";
        var markdown = await EmailMarkdownAsync(message, from, tags, log, cancellationToken);
        return await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = title,
            Icon = "✉️",
            ParentWikiPageId = folderId,
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(markdown))
        }, username, cancellationToken: cancellationToken);
    }

    // The email as a page: who sent it and when, its body, and its attachments saved as files.
    private async Task<string> EmailMarkdownAsync(MimeMessage message, MailboxAddress from, IReadOnlyList<string> tags, SentinelInboundEmail log,
        CancellationToken cancellationToken)
    {
        var sent = message.Date == DateTimeOffset.MinValue ? timeProvider.GetLocalNow() : message.Date.ToOffset(timeProvider.GetLocalNow().Offset);

        var markdown = new StringBuilder();
        markdown.Append("> Emailed by ").Append(Escape(string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} ({from.Address})"))
            .Append(" on ").Append(sent.ToGwsDateTime()).Append("\n\n");
        if (tags.Count > 0) markdown.Append(string.Join(' ', tags.Select(tag => "#" + tag))).Append("\n\n");

        var body = Body(message);
        if (body.Length > 0)
        {
            markdown.Append("---\n\n");
            markdown.Append(body.Length > SentinelEmailIn.MaxBodyLength
                ? body[..SentinelEmailIn.MaxBodyLength] + "\n\n*(Email shortened - it was too long to keep in full.)*"
                : body).Append("\n\n");
        }

        var attachments = await SaveAttachmentsAsync(message, cancellationToken);
        log.AttachmentCount = attachments.Saved.Count;
        if (attachments.Saved.Count > 0 || attachments.Skipped.Count > 0)
        {
            markdown.Append("## Attachments\n\n");
            foreach (var (file, isImage) in attachments.Saved)
            {
                var link = $"/admin/sentinel/files/{file.Id}";
                markdown.Append(isImage ? $"![{Escape(file.FileName)}]({link})" : $"[{Escape(file.FileName)}]({link})").Append("\n\n");
            }
            foreach (var skipped in attachments.Skipped) markdown.Append("*Not saved: ").Append(Escape(skipped)).Append("*\n\n");
        }
        return markdown.ToString().TrimEnd();
    }

    // ---- Email to a board (Boards K9f) ----

    // A board's own address, a plus-address on the server's mail account; null when that account
    // isn't a Google one this can read.
    public string? BoardAddress(string token) => serverAccount?.For($"{SentinelEmailIn.BoardTagPrefix}{token}")?.Address;

    // Mail to a board's address becomes a card on it. Read on the server's account whether or not
    // Sentinel's own inbox is on, since a board turns it on for itself.
    private async Task<int> PollBoardsAsync(CancellationToken cancellationToken)
    {
        if (boards is null || databases is null || serverAccount?.For(ServerAccountTag) is not { } server) return 0;
        var tokens = await BoardTokensAsync(cancellationToken);
        if (tokens.Count == 0) return 0;
        var handled = 0;
        try
        {
            var mailbox = new Mailbox(server.Host, server.Port, server.Username, server.Password, "INBOX", server.AuthServId, server.Address, true, true,
                server.OAuthCredentialId);
            using var client = await ConnectAsync(mailbox, cancellationToken);
            var folder = await client.GetFolderAsync("INBOX", cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            foreach (var (databaseId, token) in tokens)
            {
                if (BoardAddress(token) is not { } address) continue;
                foreach (var uid in (await folder.SearchAsync(ServerMailAccount.UnreadFor(address), cancellationToken)).Take(SentinelEmailIn.MaxMessagesPerPoll))
                {
                    var message = await folder.GetMessageAsync(uid, cancellationToken);
                    if (!ServerMailAccount.IsAddressedTo(message, address)) continue;
                    await ImportToBoardAsync(message, databaseId, server.AuthServId, cancellationToken);
                    await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                    handled++;
                }
            }
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Board email-in poll failed");
        }
        return handled;
    }

    private async Task<List<(Guid DatabaseId, string Token)>> BoardTokensAsync(CancellationToken cancellationToken)
    {
        var rows = await db.KanbanBoardSettings.AsNoTracking()
            .Where(row => row.SettingsJson.Contains("emailToken"))
            .Select(row => new { row.WikiDatabaseId, row.SettingsJson })
            .ToListAsync(cancellationToken);
        var live = await db.WikiDatabases.AsNoTracking().Where(database => database.TrashedAt == null).Select(database => database.Id).ToListAsync(cancellationToken);
        return rows
            .Select(row => (row.WikiDatabaseId, Token: KanbanSettings.EmailTokenOf(row.SettingsJson)))
            .Where(row => row.Token is not null && live.Contains(row.WikiDatabaseId))
            .Select(row => (row.WikiDatabaseId, row.Token!))
            .ToList();
    }

    // One message -> one log row, and a card when it's accepted: its subject read like the board's
    // quick add ("Fix login !high #bug fri"), the email as the card's page. Public for tests.
    public async Task<SentinelInboundEmail> ImportToBoardAsync(MimeMessage message, Guid databaseId, string authServId, CancellationToken cancellationToken = default)
    {
        var messageId = Truncate(string.IsNullOrWhiteSpace(message.MessageId)
            ? $"no-id:{message.Date.ToUnixTimeSeconds()}:{message.From}:{message.Subject}"
            : message.MessageId, 500);
        // The same message sent to Sentinel and a board is two imports.
        var logId = Truncate($"board:{databaseId:N}:{messageId}", 500);
        if (await db.SentinelInboundEmails.FirstOrDefaultAsync(item => item.MessageId == logId, cancellationToken) is { } existing) return existing;

        var from = message.From.Mailboxes.FirstOrDefault();
        var log = new SentinelInboundEmail
        {
            MessageId = logId,
            FromAddress = Truncate(from?.Address ?? string.Empty, 320),
            Subject = Truncate(message.Subject ?? string.Empty, 300),
            Status = SentinelEmailIn.Statuses.Rejected,
            ProcessedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            CreatedBy = SentinelEmailIn.SystemUser
        };
        db.SentinelInboundEmails.Add(log);
        try
        {
            var username = from is null ? null : await FindUserAsync(from.Address, cancellationToken);
            var board = boards is null ? null : await boards.GetBoardAsync(databaseId, cancellationToken);
            if (board is null || databases is null)
                log.Reason = "That board isn't there any more.";
            else if (from is null || message.From.Mailboxes.Count() != 1)
                log.Reason = "The message didn't have exactly one sender.";
            else if (username is null)
                log.Reason = "The sender isn't a known user (no active account has this email on its member profile).";
            else if (!SentinelEmailIn.IsAuthenticated(
                         message.Headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value),
                         authServId, from.Domain))
                log.Reason = "The mail server couldn't confirm the sender (no DMARC or DKIM pass for their domain), so it may be forged.";
            else if (!await accessService.CanAccessAsync(databaseId, true, username, SentinelAccessLevels.Edit, cancellationToken))
                log.Reason = $"The sender can't edit the board {board.Title}.";
            else
            {
                log.Username = username;
                var subject = (message.Subject ?? string.Empty).Trim();
                var card = KanbanQuickParse.Parse(subject, board, DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime), username);
                if (card.Title.Length == 0) card = card with { Title = Truncate($"Email from {from.Name ?? from.Address}", 200) };
                var column = board.Columns.FirstOrDefault(c => c.OptionId is not null && c.Group != WikiDatabaseStatusGroups.Complete) ?? board.Columns.First();
                var rowId = await boards!.AddQuickCardAsync(databaseId, column.OptionId, card with { Title = Truncate(card.Title, 300) }, username, cancellationToken);
                var markdown = await EmailMarkdownAsync(message, from, [], log, cancellationToken);
                await databases.SaveRowAsync(databaseId, new WikiDatabaseRowEditor
                {
                    Id = rowId,
                    KeepPropertyValues = true,
                    BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(markdown))
                }, username, cancellationToken);
                log.Status = SentinelEmailIn.Statuses.Imported;
                log.Reason = Truncate($"Card \"{card.Title}\" on the board {board.Title} ({column.Label}).", 500);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Importing email {MessageId} onto board {Board} failed", messageId, databaseId);
            log.Status = SentinelEmailIn.Statuses.Failed;
            log.Reason = Truncate(ex.Message, 500);
            foreach (var file in _pendingFiles) db.SentinelImportedFiles.Remove(file);
            _pendingFiles.Clear();
        }
        await db.SaveChangesAsync(cancellationToken);
        return log;
    }

    // The HTML part through the same converter as web clips (links and images limited to
    // http(s)); a plain-text email keeps its paragraphs.
    private static string Body(MimeMessage message)
    {
        var placeholder = new Uri("https://email.invalid/");
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) return WebClipMarkdownConverter.Convert(message.HtmlBody, placeholder);
        var text = (message.TextBody ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return string.Empty;
        var html = string.Concat(text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => "<p>" + string.Join("<br>", paragraph.Split('\n').Select(WebUtility.HtmlEncode)) + "</p>"));
        return WebClipMarkdownConverter.Convert(html, placeholder);
    }

    private async Task<(List<(SentinelImportedFile File, bool IsImage)> Saved, List<string> Skipped)> SaveAttachmentsAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var saved = new List<(SentinelImportedFile, bool)>();
        var skipped = new List<string>();
        long total = 0;
        var parts = message.BodyParts.OfType<MimePart>()
            .Where(part => part.IsAttachment || (part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(part.FileName)));
        foreach (var part in parts)
        {
            var name = WikiFileValues.CleanFileName(string.IsNullOrWhiteSpace(part.FileName) ? "attachment" : part.FileName);
            if (saved.Count >= SentinelEmailIn.MaxAttachments) { skipped.Add($"{name} (more than {SentinelEmailIn.MaxAttachments} attachments)"); continue; }
            using var buffer = new MemoryStream();
            if (part.Content is null) continue;
            await part.Content.DecodeToAsync(buffer, cancellationToken);
            if (buffer.Length > SentinelEmailIn.MaxAttachmentBytes) { skipped.Add($"{name} (over 10 MB)"); continue; }
            if (total + buffer.Length > SentinelEmailIn.MaxTotalAttachmentBytes) { skipped.Add($"{name} (attachments over 25 MB in total)"); continue; }
            total += buffer.Length;
            var file = new SentinelImportedFile
            {
                // Not from a Notion import: records where the file came from instead (the column
                // is unique, so each file gets its own key).
                NotionBlockId = $"email-in:{Guid.NewGuid():N}",
                FileName = name,
                ContentType = WikiFileValues.ContentTypeFor(name),
                Content = buffer.ToArray(),
                SizeBytes = buffer.Length,
                CreatedBy = SentinelEmailIn.SystemUser
            };
            db.SentinelImportedFiles.Add(file);
            _pendingFiles.Add(file);
            saved.Add((file, file.ContentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp"));
        }
        if (saved.Count > 0) await db.SaveChangesAsync(cancellationToken);
        _pendingFiles.Clear();
        return (saved, skipped);
    }

    private async Task<string?> FindUserAsync(string address, CancellationToken cancellationToken)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var userIds = await db.MemberProfiles.AsNoTracking()
            .Where(profile => profile.Email.ToLower() == normalized)
            .Select(profile => profile.AppUserId)
            .ToListAsync(cancellationToken);
        if (userIds.Count == 0) return null;
        var users = await db.AppUsers.AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && user.IsActive)
            .Select(user => new { user.Username, user.Role })
            .ToListAsync(cancellationToken);
        // Two accounts sharing one address is ambiguous - refuse rather than guess.
        var eligible = users.Where(user => AppRoles.All.Contains(user.Role)).ToList();
        return eligible.Count == 1 ? eligible[0].Username : null;
    }

    // Found by SystemKey. Created when the first accepted email arrives (an admin turned the
    // feature on); senders then need Edit on it like any other page.
    private async Task<WikiPage> EnsureFolderAsync(CancellationToken cancellationToken)
    {
        var folder = await db.WikiPages.FirstOrDefaultAsync(page => page.SystemKey == SentinelEmailIn.FolderSystemKey, cancellationToken);
        if (folder is not null)
        {
            if (folder.TrashedAt is not null) throw new SentinelEmailInException("The Inbox folder is in the trash - restore it in Sentinel.");
            return folder;
        }
        var created = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Inbox",
            Icon = "📥",
            BlocksJson = WikiBlockJson.Serialize(
            [
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Emails sent to Sentinel's mailbox show up under this page.")],
                    new Dictionary<string, string>())
            ])
        }, SentinelEmailIn.SystemUser, cancellationToken: cancellationToken);
        folder = await db.WikiPages.FirstAsync(page => page.Id == created.Id, cancellationToken);
        folder.SystemKey = SentinelEmailIn.FolderSystemKey;
        await db.SaveChangesAsync(cancellationToken);
        return folder;
    }

    // The server's account may sign in with the Google connection rather than a password.
    private Task<ImapClient> ConnectAsync(Mailbox mailbox, CancellationToken cancellationToken) =>
        mailbox.OAuthCredentialId is not null && serverAccount is not null
            ? serverAccount.ConnectAsync(new ServerMailbox(mailbox.Host, mailbox.Port, mailbox.Username, mailbox.Password, mailbox.Address, mailbox.AuthServId, mailbox.OAuthCredentialId), cancellationToken)
            : ImapMailboxConnector.ConnectAsync(mailbox.Host, mailbox.Port, mailbox.Username, mailbox.Password, cancellationToken);

    private async Task RequireAdminAsync(string username, CancellationToken cancellationToken)
    {
        if (!await db.AppUsers.AnyAsync(user => user.Username == username && user.Role == AppRoles.Admin && user.IsActive, cancellationToken))
            throw new SentinelEmailInException("Only an admin can manage email-in.");
    }

    private SentinelMailboxView ToView(SentinelMailboxSettings? saved)
    {
        var settings = saved ?? new SentinelMailboxSettings();
        var mailbox = Effective(saved);
        var onServer = mailbox is { OnlyAddressed: true };
        return new(onServer ? mailbox!.Enabled : settings.Enabled, settings.Host, settings.Port, settings.Username, settings.PasswordProtected.Length > 0,
            settings.Folder, settings.AuthServId, settings.LastPollUnix, settings.LastSuccessUnix, settings.LastError,
            onServer ? mailbox!.Address : null);
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

// Polls the mailbox every two minutes while email-in is turned on.
public sealed class SentinelEmailInBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SentinelEmailInBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var handled = await scope.ServiceProvider.GetRequiredService<ISentinelEmailInService>().PollAsync(stoppingToken);
                if (handled > 0) logger.LogInformation("Sentinel email-in handled {Count} messages", handled);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sentinel email-in poll failed");
            }
        }
    }
}
