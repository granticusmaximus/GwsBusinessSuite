using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SupportTicketService(
    IAppDbContext db,
    TimeProvider timeProvider,
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

        return await GetTicketAsync(ticketId, cancellationToken)
            ?? throw new InvalidOperationException($"Ticket {ticketId} was not found after saving.");
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
