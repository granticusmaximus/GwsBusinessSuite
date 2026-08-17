using GwsBusinessSuite.Application.Abstractions;
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
    ISecurityAuditService? securityAudit = null) : ISupportTicketService
{
    public async Task<IReadOnlyList<SupportTicketView>> ListTicketsAsync(string? statusFilter = null, CancellationToken cancellationToken = default)
    {
        var query = db.SupportTickets.AsNoTracking().Include(ticket => ticket.Messages).AsQueryable();
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
            .Include(ticket => ticket.Messages)
            .Where(ticket => ticket.ContactId == contactId)
            .ToListAsync(cancellationToken);
        return await ToViewsAsync(tickets, cancellationToken);
    }

    public async Task<SupportTicketView?> GetTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await db.SupportTickets.AsNoTracking()
            .Include(item => item.Messages)
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
        ticket.Messages.Add(new SupportTicketMessage
        {
            AuthorType = authorType,
            AuthorName = authorName,
            Body = trimmedMessage,
            CreatedAt = now,
            CreatedBy = authorName
        });
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

        return ToView(ticket, contactName);
    }

    public async Task<SupportTicketView> AddReplyAsync(
        Guid ticketId, string authorType, string authorName, string body, CancellationToken cancellationToken = default)
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
        // ticket fetched separately and untouched by Include, avoids that entirely.
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        var now = timeProvider.GetUtcNow();
        db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            TicketId = ticketId,
            AuthorType = authorType,
            AuthorName = authorName,
            Body = trimmedBody,
            CreatedAt = now,
            CreatedBy = authorName
        });
        ticket.LastRepliedAt = now;
        if (authorType == SupportTicketAuthorTypes.Contact && SupportTicketStatuses.Terminal.Contains(ticket.Status))
        {
            ticket.Status = SupportTicketStatuses.Open;
            ticket.ResolvedAt = null;
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

        var ticket = await db.SupportTickets.Include(item => item.Messages)
            .FirstOrDefaultAsync(item => item.Id == ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found.");

        var now = timeProvider.GetUtcNow();
        ticket.Status = status;
        ticket.ResolvedAt = status == SupportTicketStatuses.Resolved ? now : null;
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

        var ticket = await db.SupportTickets.Include(item => item.Messages)
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
        var ticket = await db.SupportTickets.Include(item => item.Messages)
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
            .Select(message => new SupportTicketMessageView(message.Id, message.AuthorType, message.AuthorName, message.Body, message.CreatedAt))
            .ToList(),
        ticket.CreatedAt);
}
