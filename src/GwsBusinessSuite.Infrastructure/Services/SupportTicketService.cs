using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SupportTicketService(
    IAppDbContext db,
    TimeProvider timeProvider,
    IGrowthReportEmailSender adminEmailSender,
    IClientPortalEmailSender clientPortalEmailSender,
    IOptions<SupportNotificationOptions> notificationOptions,
    ILogger<SupportTicketService> logger,
    // Optional, resolved by DI in production - see AutomationWorkflowService's own comment on
    // this same pattern for why it's nullable (existing tests new this class up directly).
    ISecurityAuditService? securityAudit = null,
    IAutomationTriggerService? automationTriggerService = null) : ISupportTicketService
{
    // Same order of magnitude as MediaLibraryService's default image cap - a ticket attachment
    // is hand-uploaded (screenshot, log file, small document), not a bulk transfer, and this
    // guards the same base64-in-a-SQLite-TEXT-column pattern against an oversized row.
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private const int MaxAttachmentsPerMessage = 5;

    private static readonly IReadOnlyDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".log"] = "text/plain",
            [".zip"] = "application/zip",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

    public async Task<IReadOnlyList<SupportTicketView>> ListTicketsAsync(string? statusFilter = null, CancellationToken cancellationToken = default)
    {
        var query = db.SupportTickets.AsNoTracking()
            .Include(ticket => ticket.Messages).ThenInclude(message => message.Attachments)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            query = query.Where(ticket => ticket.Status == statusFilter);
        }
        var tickets = await query.ToListAsync(cancellationToken);
        return await ToViewsAsync(tickets, cancellationToken);
    }

    public async Task<IReadOnlyList<SupportTicketView>> ListTicketsForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var tickets = await db.SupportTickets.AsNoTracking()
            .Include(ticket => ticket.Messages).ThenInclude(message => message.Attachments)
            .Where(ticket => ticket.ContactId == contactId)
            .ToListAsync(cancellationToken);
        return await ToViewsAsync(tickets, cancellationToken);
    }

    public async Task<SupportTicketView?> GetTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await db.SupportTickets.AsNoTracking()
            .Include(item => item.Messages).ThenInclude(message => message.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
        if (ticket is null) return null;

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == ticket.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> CreateTicketAsync(
        Guid contactId, string subject, string initialMessage, string authorType, string authorName,
        IReadOnlyList<SupportTicketAttachmentUpload>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedSubject = subject.Trim();
        var trimmedMessage = initialMessage.Trim();
        if (trimmedSubject.Length == 0)
        {
            throw new ArgumentException("A subject is required.", nameof(subject));
        }
        if (trimmedMessage.Length == 0)
        {
            throw new ArgumentException("The first message can't be empty.", nameof(initialMessage));
        }

        var contactExists = await db.Contacts.AsNoTracking()
            .AnyAsync(contact => contact.Id == contactId && contact.TrashedAt == null, cancellationToken);
        if (!contactExists)
        {
            throw new InvalidOperationException("Select an active contact for this ticket.");
        }

        var now = timeProvider.GetUtcNow();
        var ticket = new SupportTicket
        {
            ContactId = contactId,
            Subject = trimmedSubject,
            LastRepliedAt = now,
            CreatedAt = now,
            CreatedBy = authorName
        };
        // Computed from the ticket's Priority (Normal by default - CreateTicketAsync doesn't
        // take a priority parameter, only SetPriorityAsync does later) at creation time only,
        // per SupportTicketSlaTargets' own doc comment on why this isn't recomputed on change.
        var slaTargets = SupportTicketSlaTargets.For(ticket.Priority);
        ticket.FirstResponseDueAt = now + slaTargets.FirstResponse;
        ticket.ResolutionDueAt = now + slaTargets.Resolution;
        var message = new SupportTicketMessage
        {
            AuthorType = authorType,
            AuthorName = authorName,
            Body = trimmedMessage,
            CreatedAt = now,
            CreatedBy = authorName
        };
        foreach (var attachment in BuildAttachments(attachments, authorName))
        {
            message.Attachments.Add(attachment);
        }
        ticket.Messages.Add(message);
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);

        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "SupportTicketCreated", SecurityAuditOutcomes.Succeeded,
                TargetType: "SupportTicket", TargetId: ticket.Id.ToString(),
                Details: new Dictionary<string, string?> { ["contactId"] = contactId.ToString() }), cancellationToken);
        }

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == contactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";

        // Only when the CONTACT raised it themselves through the portal - staff opening a
        // ticket on a contact's behalf shouldn't email themselves about their own action.
        if (authorType == SupportTicketAuthorTypes.Contact)
        {
            try
            {
                await NotifyAdminOfNewTicketAsync(ticket, contactName, trimmedMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send a new-ticket notification email for ticket {TicketId}.", ticket.Id);
            }
        }

        if (automationTriggerService is not null)
        {
            try
            {
                await automationTriggerService.TriggerSupportTicketCreatedAsync(
                    ticket.Id, ticket.Subject, contactName, ticket.Priority, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Support ticket-created automation trigger failed for ticket {TicketId}.", ticket.Id);
            }
        }

        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> AddReplyAsync(
        Guid ticketId, string authorType, string authorName, string body,
        IReadOnlyList<SupportTicketAttachmentUpload>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedBody = body.Trim();
        if (trimmedBody.Length == 0)
        {
            throw new ArgumentException("A reply can't be empty.", nameof(body));
        }

        // Deliberately fetched WITHOUT Include(Messages) here - appending a new child to a
        // collection navigation that was populated by an earlier, separate Include() query
        // (which every call to this method would otherwise do, against the same long-lived
        // scoped DbContext) confuses EF Core's change tracker into treating the new row as
        // already-persisted, so it emits an UPDATE instead of an INSERT and then fails as a
        // bogus concurrency conflict. Adding the message directly on its own DbSet, with the
        // ticket fetched separately and untouched by Include, avoids that entirely. Same reason
        // attachments are added via their own DbSet below rather than through the message's
        // Attachments navigation.
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        var now = timeProvider.GetUtcNow();
        var message = new SupportTicketMessage
        {
            TicketId = ticketId,
            AuthorType = authorType,
            AuthorName = authorName,
            Body = trimmedBody,
            CreatedAt = now,
            CreatedBy = authorName
        };
        db.SupportTicketMessages.Add(message);
        foreach (var attachment in BuildAttachments(attachments, authorName))
        {
            attachment.MessageId = message.Id;
            db.SupportTicketAttachments.Add(attachment);
        }
        ticket.LastRepliedAt = now;
        if (authorType == SupportTicketAuthorTypes.Contact && SupportTicketStatuses.Terminal.Contains(ticket.Status))
        {
            ticket.Status = SupportTicketStatuses.Open;
            ReopenSlaClock(ticket, now);
        }
        ticket.UpdatedAt = now;
        ticket.UpdatedBy = authorName;
        await db.SaveChangesAsync(cancellationToken);

        // A notification failure must never take down the reply itself - the message is
        // already safely saved by this point regardless of what happens next.
        try
        {
            if (authorType == SupportTicketAuthorTypes.Staff)
            {
                await NotifyContactOfReplyAsync(ticket, cancellationToken);
            }
            else
            {
                await NotifyAdminOfReplyAsync(ticket, authorName, trimmedBody, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send a reply notification email for ticket {TicketId}.", ticketId);
        }

        if (automationTriggerService is not null)
        {
            try
            {
                await automationTriggerService.TriggerSupportTicketRepliedAsync(
                    ticketId, authorType, authorName, trimmedBody, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Support ticket-replied automation trigger failed for ticket {TicketId}.", ticketId);
            }
        }

        return await GetTicketAsync(ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found after saving.");
    }

    private async Task NotifyAdminOfNewTicketAsync(
        SupportTicket ticket, string contactName, string initialMessage, CancellationToken cancellationToken)
    {
        if (!adminEmailSender.Configuration.IsConfigured) return;

        var portalUrl = $"{notificationOptions.Value.AdminBaseUrl.TrimEnd('/')}/admin/support";
        var subject = $"New support ticket: {ticket.Subject}";
        var plainText = $"{contactName} opened a new support ticket.\n\nSubject: {ticket.Subject}\n\n{initialMessage}\n\nView it here: {portalUrl}";
        var html = $"<p><strong>{System.Net.WebUtility.HtmlEncode(contactName)}</strong> opened a new support ticket.</p>" +
            $"<p><strong>Subject:</strong> {System.Net.WebUtility.HtmlEncode(ticket.Subject)}</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(initialMessage)}</p>" +
            $"""<p><a href="{portalUrl}">View it in the admin inbox</a></p>""";
        await adminEmailSender.SendAsync(
            new GrowthReportEmail(notificationOptions.Value.NotifyEmail, subject, plainText, html), cancellationToken);
    }

    private async Task NotifyAdminOfReplyAsync(
        SupportTicket ticket, string authorName, string body, CancellationToken cancellationToken)
    {
        if (!adminEmailSender.Configuration.IsConfigured) return;

        var portalUrl = $"{notificationOptions.Value.AdminBaseUrl.TrimEnd('/')}/admin/support";
        var subject = $"New reply on ticket: {ticket.Subject}";
        var plainText = $"{authorName} replied to \"{ticket.Subject}\".\n\n{body}\n\nView it here: {portalUrl}";
        var html = $"<p><strong>{System.Net.WebUtility.HtmlEncode(authorName)}</strong> replied to <strong>{System.Net.WebUtility.HtmlEncode(ticket.Subject)}</strong>.</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>" +
            $"""<p><a href="{portalUrl}">View it in the admin inbox</a></p>""";
        await adminEmailSender.SendAsync(
            new GrowthReportEmail(notificationOptions.Value.NotifyEmail, subject, plainText, html), cancellationToken);
    }

    // The contact may not have an email on file (Contact.Email is nullable) - skip gracefully,
    // same "opt-in, never crash" posture as every other notification path in this method.
    private async Task NotifyContactOfReplyAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.AsNoTracking()
            .Where(c => c.Id == ticket.ContactId)
            .Select(c => new { c.Email, c.FullName })
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(contact?.Email)) return;

        var portalUrl = $"{notificationOptions.Value.AdminBaseUrl.TrimEnd('/')}/client-portal/support";
        await clientPortalEmailSender.SendTicketReplyNotificationAsync(
            contact.Email, contact.FullName, ticket.Subject, portalUrl, cancellationToken);
    }

    public async Task<SupportTicketView> SetStatusAsync(Guid ticketId, string status, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!SupportTicketStatuses.All.Contains(status))
        {
            throw new ArgumentException($"'{status}' is not a valid ticket status.", nameof(status));
        }

        var ticket = await db.SupportTickets.Include(item => item.Messages).ThenInclude(message => message.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        var now = timeProvider.GetUtcNow();
        var wasTerminal = SupportTicketStatuses.Terminal.Contains(ticket.Status);
        ticket.Status = status;
        ticket.ResolvedAt = status == SupportTicketStatuses.Resolved ? now : null;
        if (wasTerminal && !SupportTicketStatuses.Terminal.Contains(status))
        {
            ReopenSlaClock(ticket, now);
        }
        ticket.UpdatedAt = now;
        ticket.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == ticket.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> SetPriorityAsync(Guid ticketId, string priority, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!SupportTicketPriorities.All.Contains(priority))
        {
            throw new ArgumentException($"'{priority}' is not a valid ticket priority.", nameof(priority));
        }

        var ticket = await db.SupportTickets.Include(item => item.Messages).ThenInclude(message => message.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        ticket.Priority = priority;
        ticket.UpdatedAt = timeProvider.GetUtcNow();
        ticket.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == ticket.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> AssignAsync(Guid ticketId, string? assignedToUsername, string performedBy, CancellationToken cancellationToken = default)
    {
        var ticket = await db.SupportTickets.Include(item => item.Messages).ThenInclude(message => message.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        ticket.AssignedToUsername = string.IsNullOrWhiteSpace(assignedToUsername) ? null : assignedToUsername.Trim();
        ticket.UpdatedAt = timeProvider.GetUtcNow();
        ticket.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == ticket.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> SetTagsAsync(Guid ticketId, string tagsCsv, string performedBy, CancellationToken cancellationToken = default)
    {
        var ticket = await db.SupportTickets.Include(item => item.Messages).ThenInclude(message => message.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        ticket.TagsCsv = string.Join(", ", tagsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        ticket.UpdatedAt = timeProvider.GetUtcNow();
        ticket.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == ticket.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";
        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> SubmitSatisfactionRatingAsync(
        Guid ticketId, int rating, string? comment, CancellationToken cancellationToken = default)
    {
        if (rating is < 1 or > 5)
        {
            throw new ArgumentException("Rating must be between 1 and 5.", nameof(rating));
        }

        var ticket = await db.SupportTickets.Include(item => item.Messages).ThenInclude(message => message.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");
        if (ticket.Status != SupportTicketStatuses.Resolved)
        {
            throw new InvalidOperationException("Only a resolved ticket can be rated.");
        }
        if (ticket.SatisfactionRating.HasValue)
        {
            throw new InvalidOperationException("This ticket has already been rated.");
        }

        var contactName = await db.Contacts.AsNoTracking()
            .Where(contact => contact.Id == ticket.ContactId)
            .Select(contact => contact.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown contact";

        ticket.SatisfactionRating = rating;
        ticket.SatisfactionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        ticket.UpdatedAt = timeProvider.GetUtcNow();
        ticket.UpdatedBy = contactName;
        await db.SaveChangesAsync(cancellationToken);

        return ToView(ticket, contactName);
    }

    private async Task<IReadOnlyList<SupportTicketView>> ToViewsAsync(List<SupportTicket> tickets, CancellationToken cancellationToken)
    {
        var contactIds = tickets.Select(ticket => ticket.ContactId).Distinct().ToList();
        var contactNames = await db.Contacts.AsNoTracking()
            .Where(contact => contactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);

        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column - sort client-side.
        return tickets
            .OrderByDescending(ticket => ticket.LastRepliedAt ?? ticket.CreatedAt)
            .Select(ticket => ToView(ticket, contactNames.GetValueOrDefault(ticket.ContactId, "Unknown contact")))
            .ToList();
    }

    // Reopening a terminal ticket used to only clear Status/ResolvedAt, leaving
    // FirstResponseDueAt/ResolutionDueAt anchored to the ticket's original creation time and
    // both *BreachNotifiedAt flags untouched. That produced two real bugs: a ticket resolved
    // well within SLA and reopened days later immediately looked breached (stale due date
    // already in the past) on the very next sweep, and a ticket that had ever legitimately
    // breached once could never trigger a breach notification again after being reopened,
    // since ProcessSlaBreachesAsync's `is null` guard on *BreachNotifiedAt would stay
    // permanently set. Recomputing both due dates from "now" and clearing both flags gives a
    // reopened ticket a fresh SLA clock, same as if it were a new ticket at this priority.
    private static void ReopenSlaClock(SupportTicket ticket, DateTimeOffset now)
    {
        ticket.ResolvedAt = null;
        var slaTargets = SupportTicketSlaTargets.For(ticket.Priority);
        ticket.FirstResponseDueAt = now + slaTargets.FirstResponse;
        ticket.ResolutionDueAt = now + slaTargets.Resolution;
        ticket.FirstResponseBreachNotifiedAt = null;
        ticket.ResolutionBreachNotifiedAt = null;
    }

    public async Task<int> ProcessSlaBreachesAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await db.SupportTickets
            .Include(ticket => ticket.Messages)
            .Where(ticket => ticket.Status == SupportTicketStatuses.Open || ticket.Status == SupportTicketStatuses.Pending)
            .ToListAsync(cancellationToken);
        var contactIds = candidates.Select(ticket => ticket.ContactId).Distinct().ToList();
        var contactNames = await db.Contacts.AsNoTracking()
            .Where(contact => contactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);

        var breaches = new List<(SupportTicket Ticket, string Type, DateTimeOffset DueAt)>();
        foreach (var ticket in candidates)
        {
            var hasStaffResponse = ticket.Messages.Any(message => message.AuthorType == SupportTicketAuthorTypes.Staff);
            if (!hasStaffResponse && ticket.FirstResponseBreachNotifiedAt is null
                && ticket.FirstResponseDueAt is { } firstDue && firstDue < now)
            {
                ticket.FirstResponseBreachNotifiedAt = now;
                breaches.Add((ticket, "FirstResponse", firstDue));
            }

            if (ticket.ResolutionBreachNotifiedAt is null
                && ticket.ResolutionDueAt is { } resolutionDue && resolutionDue < now)
            {
                ticket.ResolutionBreachNotifiedAt = now;
                breaches.Add((ticket, "Resolution", resolutionDue));
            }
        }

        if (breaches.Count == 0) return 0;
        await db.SaveChangesAsync(cancellationToken);
        if (automationTriggerService is not null)
        {
            foreach (var breach in breaches)
            {
                await automationTriggerService.TriggerSupportTicketSlaBreachedAsync(
                    breach.Ticket.Id, breach.Ticket.Subject,
                    contactNames.GetValueOrDefault(breach.Ticket.ContactId, "Unknown contact"),
                    breach.Ticket.Priority, breach.Type, breach.DueAt, cancellationToken);
            }
        }

        return breaches.Count;
    }

    private static SupportTicketView ToView(SupportTicket ticket, string contactName) => new(
        ticket.Id,
        ticket.ContactId,
        contactName,
        ticket.Subject,
        ticket.Status,
        ticket.Priority,
        ticket.AssignedToUsername,
        ticket.LastRepliedAt,
        ticket.ResolvedAt,
        ticket.Messages
            .OrderBy(message => message.CreatedAt)
            .Select(message => new SupportTicketMessageView(
                message.Id, message.AuthorType, message.AuthorName, message.Body, message.CreatedAt,
                message.Attachments
                    .Select(attachment => new SupportTicketAttachmentView(
                        attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes))
                    .ToList()))
            .ToList(),
        ticket.CreatedAt,
        ticket.TagsCsv,
        ticket.FirstResponseDueAt,
        ticket.ResolutionDueAt,
        ticket.SatisfactionRating,
        ticket.SatisfactionComment);

    private static IReadOnlyList<SupportTicketAttachment> BuildAttachments(
        IReadOnlyList<SupportTicketAttachmentUpload>? uploads, string authorName)
    {
        if (uploads is null || uploads.Count == 0)
        {
            return [];
        }
        if (uploads.Count > MaxAttachmentsPerMessage)
        {
            throw new ArgumentException($"A reply can have at most {MaxAttachmentsPerMessage} attachments.", nameof(uploads));
        }

        var attachments = new List<SupportTicketAttachment>(uploads.Count);
        foreach (var upload in uploads)
        {
            if (string.IsNullOrWhiteSpace(upload.FileName))
            {
                throw new ArgumentException("An attachment must have a file name.", nameof(uploads));
            }
            if (upload.Content.Length == 0)
            {
                throw new ArgumentException($"'{upload.FileName}' is empty.", nameof(uploads));
            }
            if (upload.Content.Length > MaxAttachmentBytes)
            {
                throw new ArgumentException(
                    $"'{upload.FileName}' exceeds the {MaxAttachmentBytes / 1024 / 1024} MB attachment limit.", nameof(uploads));
            }

            var extension = Path.GetExtension(upload.FileName);
            var contentType = extension is not null && ContentTypesByExtension.TryGetValue(extension, out var mapped)
                ? mapped
                : "application/octet-stream";

            attachments.Add(new SupportTicketAttachment
            {
                FileName = upload.FileName.Trim(),
                ContentType = contentType,
                DataUri = $"data:{contentType};base64,{Convert.ToBase64String(upload.Content)}",
                SizeBytes = upload.Content.Length,
                CreatedBy = authorName
            });
        }
        return attachments;
    }

    public async Task<(string FileName, string ContentType, byte[] Content)?> GetAttachmentContentAsync(
        Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await db.SupportTicketAttachments.AsNoTracking()
            .Where(item => item.Id == attachmentId)
            .Select(item => new { item.FileName, item.ContentType, item.DataUri })
            .FirstOrDefaultAsync(cancellationToken);
        if (attachment is null) return null;

        var base64 = attachment.DataUri[(attachment.DataUri.IndexOf(',') + 1)..];
        return (attachment.FileName, attachment.ContentType, Convert.FromBase64String(base64));
    }

    public async Task<Guid?> GetAttachmentOwnerContactIdAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var ticketId = await db.SupportTicketAttachments.AsNoTracking()
            .Where(attachment => attachment.Id == attachmentId)
            .Select(attachment => attachment.Message!.TicketId)
            .FirstOrDefaultAsync(cancellationToken);
        if (ticketId == Guid.Empty) return null;

        var contactId = await db.SupportTickets.AsNoTracking()
            .Where(ticket => ticket.Id == ticketId)
            .Select(ticket => (Guid?)ticket.ContactId)
            .FirstOrDefaultAsync(cancellationToken);
        return contactId;
    }
}
