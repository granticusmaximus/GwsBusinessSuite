using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using MailKit;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Email-to-ticket (see SupportEmailIn for the rules). Same mailbox handling as Sentinel's
// email-in: IMAP polling, an encrypted app password, and a log row per message keyed by
// Message-ID so nothing is processed twice.
public sealed class SupportEmailInService(
    IAppDbContext db,
    ISupportTicketService tickets,
    ICrmService crm,
    IClientPortalEmailSender contactEmail,
    IOptions<SupportNotificationOptions> notificationOptions,
    ISecretProtector secretProtector,
    TimeProvider timeProvider,
    ILogger<SupportEmailInService> logger,
    ServerMailAccount? serverAccount = null) : ISupportEmailInService
{
    private const int MaxAttachments = 5;
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private const string ServerAccountTag = "support";

    // What the poller reads. A mailbox saved in the panel (with its app password) wins; otherwise
    // the server's own mail account on its plus-address (see ServerMailAccount), which is on until
    // it's turned off here. OnlyAddressed: read only mail sent to that address.
    private sealed record Mailbox(string Host, int Port, string Username, string Password, string Folder, string AuthServId,
        string Address, bool OnlyAddressed, bool Enabled);

    private Mailbox? Effective(SupportMailboxSettings? settings)
    {
        if (settings is { PasswordProtected.Length: > 0 })
            return new Mailbox(settings.Host, settings.Port, settings.Username, secretProtector.Unprotect(settings.PasswordProtected),
                settings.Folder, settings.AuthServId, settings.Username, false, settings.Enabled);
        if (serverAccount?.For(ServerAccountTag) is not { } server) return null;
        return new Mailbox(server.Host, server.Port, server.Username, server.Password, "INBOX", server.AuthServId, server.Address, true,
            settings?.Enabled ?? true);
    }

    public async Task<SupportMailboxView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        ToView(await db.SupportMailboxSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken));

    public async Task<SupportMailboxView> SetServerAccountEnabledAsync(bool enabled, string performedBy, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(performedBy, cancellationToken);
        var settings = await db.SupportMailboxSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is { PasswordProtected.Length: > 0 }) throw new SupportEmailInException("A mailbox is saved here, so email-to-ticket uses that one.");
        if (serverAccount?.For(ServerAccountTag) is null) throw new SupportEmailInException("The server's mail account isn't a Google account this can read.");
        if (settings is null)
        {
            settings = new SupportMailboxSettings { CreatedBy = performedBy };
            db.SupportMailboxSettings.Add(settings);
        }
        settings.Enabled = enabled;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(settings);
    }

    public async Task<SupportMailboxView> SaveSettingsAsync(SupportMailboxUpdate update, string performedBy, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(performedBy, cancellationToken);
        var host = (update.Host ?? string.Empty).Trim();
        var username = (update.Username ?? string.Empty).Trim();
        var folder = string.IsNullOrWhiteSpace(update.Folder) ? "INBOX" : update.Folder.Trim();
        if (host.Length is 0 or > 200 || host.Contains(' ') || host.Contains('/')) throw new SupportEmailInException("Enter the IMAP server's host name, e.g. imap.gmail.com.");
        if (update.Port is < 1 or > 65535) throw new SupportEmailInException("The port must be between 1 and 65535 (IMAP over TLS is 993).");
        if (username.Length is 0 or > 320 || !username.Contains('@')) throw new SupportEmailInException("Enter the support mailbox's full address, e.g. support@gwsapp.net - clients reply to it.");
        if (string.IsNullOrWhiteSpace(update.AuthServId)) throw new SupportEmailInException("Enter the receiving server's name (mx.google.com for Google Workspace).");

        var settings = await db.SupportMailboxSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new SupportMailboxSettings { CreatedBy = performedBy };
            db.SupportMailboxSettings.Add(settings);
        }
        settings.Host = host;
        settings.Port = update.Port;
        settings.Username = username;
        settings.Folder = folder;
        settings.AuthServId = update.AuthServId.Trim();
        if (!string.IsNullOrEmpty(update.Password)) settings.PasswordProtected = secretProtector.Protect(update.Password.Replace(" ", string.Empty));
        if (update.Enabled && settings.PasswordProtected.Length == 0) throw new SupportEmailInException("Enter the mailbox's app password before turning email-to-ticket on.");
        settings.Enabled = update.Enabled;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(settings);
    }

    public async Task<string> TestConnectionAsync(string performedBy, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(performedBy, cancellationToken);
        var mailbox = Effective(await db.SupportMailboxSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken))
            ?? throw new SupportEmailInException("Save the mailbox settings first.");
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
        catch (Exception ex) when (ex is not SupportEmailInException and not OperationCanceledException)
        {
            throw new SupportEmailInException($"Couldn't connect: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SupportInboundEmailView>> ListRecentAsync(int count, CancellationToken cancellationToken = default) =>
        await db.SupportInboundEmails.AsNoTracking()
            .OrderByDescending(item => item.ProcessedAtUnix)
            .Take(Math.Clamp(count, 1, 200))
            .Select(item => new SupportInboundEmailView(item.Id, item.FromAddress, item.Subject, item.Status, item.Reason, item.TicketId, item.ProcessedAtUnix))
            .ToListAsync(cancellationToken);

    public async Task<string?> GetReplyToAddressAsync(CancellationToken cancellationToken = default)
    {
        var mailbox = Effective(await db.SupportMailboxSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken));
        return mailbox is { Enabled: true } ? mailbox.Address : null;
    }

    public async Task<int> PollAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.SupportMailboxSettings.FirstOrDefaultAsync(cancellationToken);
        if (Effective(settings) is not { Enabled: true } mailbox) return 0;
        if (settings is null)
        {
            // The server's account needs somewhere to keep its last poll and error.
            settings = new SupportMailboxSettings { Enabled = true, CreatedBy = "system" };
            db.SupportMailboxSettings.Add(settings);
        }
        settings.LastPollUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var handled = 0;
        try
        {
            using var client = await ConnectAsync(mailbox, cancellationToken);
            var folder = await client.GetFolderAsync(mailbox.Folder, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            var unread = await folder.SearchAsync(mailbox.OnlyAddressed ? ServerMailAccount.UnreadFor(mailbox.Address) : SearchQuery.NotSeen, cancellationToken);
            foreach (var uid in unread.Take(SupportEmailIn.MaxMessagesPerPoll))
            {
                var message = await folder.GetMessageAsync(uid, cancellationToken);
                // Anything not sent to the plus-address is left exactly as it was (still unread).
                if (mailbox.OnlyAddressed && !ServerMailAccount.IsAddressedTo(message, mailbox.Address)) continue;
                await ImportAsync(message, mailbox.AuthServId, mailbox.Address, cancellationToken);
                // Marked read only after it's recorded: a crash mid-message retries it, and the
                // Message-ID log stops a duplicate ticket.
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                handled++;
            }
            await client.DisconnectAsync(true, cancellationToken);
            settings.LastSuccessUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            settings.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Support email-to-ticket poll failed");
            settings.LastError = Truncate(ex.Message, 500);
        }
        await db.SaveChangesAsync(cancellationToken);
        return handled;
    }

    // One message -> one log row, and a new ticket or a reply on an existing one. Public for tests.
    public async Task<SupportInboundEmail> ImportAsync(MimeMessage message, string authServId, string mailboxAddress, CancellationToken cancellationToken = default)
    {
        var messageId = Truncate(string.IsNullOrWhiteSpace(message.MessageId)
            ? $"no-id:{message.Date.ToUnixTimeSeconds()}:{message.From}:{message.Subject}"
            : message.MessageId, 500);
        var existing = await db.SupportInboundEmails.FirstOrDefaultAsync(item => item.MessageId == messageId, cancellationToken);
        if (existing is not null) return existing;

        var from = message.From.Mailboxes.FirstOrDefault();
        var log = new SupportInboundEmail
        {
            MessageId = messageId,
            FromAddress = Truncate(from?.Address ?? string.Empty, 320),
            Subject = Truncate(message.Subject ?? string.Empty, 300),
            Status = SupportEmailIn.Statuses.Skipped,
            ProcessedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            CreatedBy = SupportEmailIn.SystemUser
        };
        db.SupportInboundEmails.Add(log);

        try
        {
            if (from is null || message.From.Mailboxes.Count() != 1)
                log.Reason = "The message didn't have exactly one sender.";
            else if (string.Equals(from.Address, mailboxAddress, StringComparison.OrdinalIgnoreCase))
                log.Reason = "Sent from the support mailbox itself.";
            else if (SupportEmailIn.IsAutomatic(message.Headers.Select(h => new KeyValuePair<string, string>(h.Field, h.Value)), from.Address))
                log.Reason = "An automatic message (out-of-office, bounce or mailing list), so no ticket.";
            else
                await HandleAsync(message, from, authServId, mailboxAddress, log, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Email-to-ticket for {MessageId} failed", messageId);
            // Anything half-made (a ticket that failed to save) mustn't ride along with the log row.
            if (db is DbContext context)
            {
                foreach (var entry in context.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added && entry.Entity != log).ToList())
                    entry.State = EntityState.Detached;
            }
            log.Status = SupportEmailIn.Statuses.Failed;
            log.Reason = Truncate(ex.Message, 500);
        }
        await db.SaveChangesAsync(cancellationToken);
        return log;
    }

    private async Task HandleAsync(MimeMessage message, MailboxAddress from, string authServId, string mailboxAddress,
        SupportInboundEmail log, CancellationToken cancellationToken)
    {
        // Only the receiving server's own DMARC/DKIM verdict is trusted (same check as Sentinel).
        var authenticated = SentinelEmailIn.IsAuthenticated(
            message.Headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value),
            authServId, from.Domain);
        var senderName = string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name.Trim();
        var (attachments, skipped) = Attachments(message);
        var body = Body(message, skipped);

        // A reply: the tag names one of this sender's tickets. Forged mail can't add to a ticket.
        if (SupportEmailIn.ParseTicketReference(message.Subject) is { } reference)
        {
            if (!authenticated)
            {
                log.Reason = "Had a ticket tag but the sender couldn't be verified, so it became a new ticket.";
            }
            else if (await FindTicketAsync(from.Address, reference, cancellationToken) is { } ticketId)
            {
                await tickets.AddReplyAsync(ticketId, SupportTicketAuthorTypes.Contact, senderName,
                    SupportEmailIn.StripQuotedReply(body) is { Length: > 0 } reply ? reply : body, attachments, cancellationToken);
                log.Status = SupportEmailIn.Statuses.Replied;
                log.TicketId = ticketId;
                return;
            }
            else
            {
                log.Reason = "The ticket tag didn't match one of this sender's tickets, so it became a new ticket.";
            }
        }

        var contact = await crm.FindOrCreateContactAsync(from.Address, senderName, null, "support-email", cancellationToken);
        var subject = SupportEmailIn.CleanSubject(message.Subject) is { Length: > 0 } clean ? Truncate(clean, 200) : $"Email from {senderName}";
        var ticket = await tickets.CreateTicketAsync(contact.Id, subject, body, SupportTicketAuthorTypes.Contact, senderName, attachments, cancellationToken);
        await tickets.SetTagsAsync(ticket.Id, authenticated ? "email" : "email, unverified-sender", SupportEmailIn.SystemUser, cancellationToken);
        log.Status = SupportEmailIn.Statuses.Created;
        log.TicketId = ticket.Id;

        // A receipt with the tag, so their next email joins this ticket. Only to verified senders:
        // never send mail on behalf of a forged From address.
        if (authenticated)
        {
            var portalUrl = $"{notificationOptions.Value.AdminBaseUrl.TrimEnd('/')}/client-portal/support";
            await contactEmail.SendTicketReplyNotificationAsync(from.Address, senderName, subject, portalUrl, cancellationToken,
                new TicketReplyEmail("Thanks - we've received your message and opened a support ticket. We'll reply here.",
                    SupportEmailIn.SubjectTag(ticket.Id), mailboxAddress));
        }
    }

    private async Task<Guid?> FindTicketAsync(string senderAddress, string reference, CancellationToken cancellationToken)
    {
        var contactIds = await db.Contacts.AsNoTracking()
            .Where(contact => contact.TrashedAt == null && contact.Email != null && contact.Email.ToLower() == senderAddress.ToLower())
            .Select(contact => contact.Id)
            .ToListAsync(cancellationToken);
        if (contactIds.Count == 0) return null;
        var ticketIds = await db.SupportTickets.AsNoTracking()
            .Where(ticket => contactIds.Contains(ticket.ContactId))
            .Select(ticket => ticket.Id)
            .ToListAsync(cancellationToken);
        return ticketIds.Where(id => SupportEmailIn.TicketReference(id) == reference).Select(id => (Guid?)id).FirstOrDefault();
    }

    // Plain text when the email has it (best for spotting the quoted history); otherwise the HTML
    // part through the same safe converter as Sentinel's email-in.
    private static string Body(MimeMessage message, IReadOnlyList<string> skippedAttachments)
    {
        var text = (message.TextBody ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length == 0 && !string.IsNullOrWhiteSpace(message.HtmlBody))
            text = WebClipMarkdownConverter.Convert(message.HtmlBody, new Uri("https://email.invalid/")).Trim();
        if (text.Length > SupportEmailIn.MaxBodyLength)
            text = text[..SupportEmailIn.MaxBodyLength] + "\n\n*(Email shortened - it was too long to keep in full.)*";
        var builder = new StringBuilder(text.Length == 0 ? "*(The email had no text.)*" : text);
        foreach (var skipped in skippedAttachments) builder.Append("\n\n*Not attached: ").Append(skipped).Append('*');
        return builder.ToString();
    }

    private static (List<SupportTicketAttachmentUpload> Saved, List<string> Skipped) Attachments(MimeMessage message)
    {
        var saved = new List<SupportTicketAttachmentUpload>();
        var skipped = new List<string>();
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            var name = Truncate(part.FileName ?? "attachment", 200);
            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            if (saved.Count >= MaxAttachments) skipped.Add($"{name} (more than {MaxAttachments} attachments)");
            else if (stream.Length > MaxAttachmentBytes) skipped.Add($"{name} (over {MaxAttachmentBytes / 1024 / 1024} MB)");
            else if (stream.Length == 0) skipped.Add($"{name} (empty)");
            else saved.Add(new SupportTicketAttachmentUpload(name, stream.ToArray()));
        }
        return (saved, skipped);
    }

    private static Task<MailKit.Net.Imap.ImapClient> ConnectAsync(Mailbox mailbox, CancellationToken cancellationToken) =>
        ImapMailboxConnector.ConnectAsync(mailbox.Host, mailbox.Port, mailbox.Username, mailbox.Password, cancellationToken);

    private async Task RequireAdminAsync(string username, CancellationToken cancellationToken)
    {
        if (!await db.AppUsers.AnyAsync(user => user.Username == username && user.Role == AppRoles.Admin && user.IsActive, cancellationToken))
            throw new SupportEmailInException("Only an admin can manage email-to-ticket.");
    }

    private SupportMailboxView ToView(SupportMailboxSettings? saved)
    {
        var settings = saved ?? new SupportMailboxSettings();
        var mailbox = Effective(saved);
        var onServer = mailbox is { OnlyAddressed: true };
        return new(onServer ? mailbox!.Enabled : settings.Enabled, settings.Host, settings.Port, settings.Username, settings.PasswordProtected.Length > 0,
            settings.Folder, settings.AuthServId, settings.LastPollUnix, settings.LastSuccessUnix, settings.LastError,
            onServer ? mailbox!.Address : null);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

// Checks the support mailbox every two minutes while email-to-ticket is on.
public sealed class SupportEmailInBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SupportEmailInBackgroundService> logger) : BackgroundService
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
                var handled = await scope.ServiceProvider.GetRequiredService<ISupportEmailInService>().PollAsync(stoppingToken);
                if (handled > 0) logger.LogInformation("Email-to-ticket handled {Count} messages", handled);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Support email-to-ticket poll failed");
            }
        }
    }
}
