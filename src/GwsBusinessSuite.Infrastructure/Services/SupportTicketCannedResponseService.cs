using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SupportTicketCannedResponseService(IAppDbContext db, TimeProvider timeProvider)
    : ISupportTicketCannedResponseService
{
    public async Task<IReadOnlyList<SupportTicketCannedResponseView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var responses = await db.SupportTicketCannedResponses.AsNoTracking().ToListAsync(cancellationToken);
        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column - sort client-side,
        // same convention as SupportTicketService.ToViewsAsync.
        return responses
            .OrderBy(response => response.Title, StringComparer.OrdinalIgnoreCase)
            .Select(ToView)
            .ToList();
    }

    public async Task<SupportTicketCannedResponseView> CreateAsync(
        string title, string body, string performedBy, CancellationToken cancellationToken = default)
    {
        var trimmedTitle = title.Trim();
        var trimmedBody = body.Trim();
        if (trimmedTitle.Length == 0)
        {
            throw new ArgumentException("A title is required.", nameof(title));
        }
        if (trimmedBody.Length == 0)
        {
            throw new ArgumentException("A body is required.", nameof(body));
        }

        var response = new SupportTicketCannedResponse
        {
            Title = trimmedTitle,
            Body = trimmedBody,
            CreatedBy = performedBy
        };
        db.SupportTicketCannedResponses.Add(response);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(response);
    }

    public async Task<SupportTicketCannedResponseView> UpdateAsync(
        Guid id, string title, string body, string performedBy, CancellationToken cancellationToken = default)
    {
        var trimmedTitle = title.Trim();
        var trimmedBody = body.Trim();
        if (trimmedTitle.Length == 0)
        {
            throw new ArgumentException("A title is required.", nameof(title));
        }
        if (trimmedBody.Length == 0)
        {
            throw new ArgumentException("A body is required.", nameof(body));
        }

        var response = await db.SupportTicketCannedResponses.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Canned response {id} was not found.");

        response.Title = trimmedTitle;
        response.Body = trimmedBody;
        response.UpdatedAt = timeProvider.GetUtcNow();
        response.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(response);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await db.SupportTicketCannedResponses.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (response is null) return;

        db.SupportTicketCannedResponses.Remove(response);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static SupportTicketCannedResponseView ToView(SupportTicketCannedResponse response) => new(
        response.Id, response.Title, response.Body);
}
