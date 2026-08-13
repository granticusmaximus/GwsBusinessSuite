using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class ClientPortalAuthService(
    IAppDbContext dbContext,
    IClientPortalEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<ClientPortalAuthService> logger) : IClientPortalAuthService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public async Task RequestLoginLinkAsync(
        string email,
        string loginBaseUrl,
        string? requestedFromIp = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedEmail = email.Trim();
        if (trimmedEmail.Length == 0) return;

        // SQLite/EF Core translates ToLower() to a real SQL LOWER() comparison, so this stays a
        // server-side, case-insensitive lookup rather than materializing every contact.
        var contact = await dbContext.Contacts.AsNoTracking()
            .Where(item => item.TrashedAt == null && item.Email != null)
            .FirstOrDefaultAsync(item => item.Email!.ToLower() == trimmedEmail.ToLower(), cancellationToken);
        if (contact is null || string.IsNullOrWhiteSpace(contact.Email))
        {
            // Deliberately no-op, no exception, no distinguishable timing - never confirm or
            // deny whether an email address belongs to a real contact.
            logger.LogInformation("Client portal login link requested for an email with no matching contact.");
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var now = timeProvider.GetUtcNow();
        await dbContext.ClientPortalLoginTokens.AddAsync(new ClientPortalLoginToken
        {
            ContactId = contact.Id,
            TokenHash = HashToken(token),
            ExpiresAt = now.Add(TokenLifetime),
            RequestedFromIp = requestedFromIp,
            CreatedAt = now,
            CreatedBy = "client-portal-login"
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var loginUrl = $"{loginBaseUrl.TrimEnd('/')}?token={Uri.EscapeDataString(token)}";
        await emailSender.SendLoginLinkAsync(contact.Email, contact.FullName, loginUrl, cancellationToken);
    }

    public async Task<ClientPortalContactView?> ConsumeLoginLinkAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = HashToken(token.Trim());
        var record = await dbContext.ClientPortalLoginTokens.FirstOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (record is null || record.ConsumedAt is not null || record.ExpiresAt < now)
        {
            return null;
        }

        // Marked consumed even if the contact lookup below fails (e.g. trashed in the meantime)
        // - a token is single-use regardless of what happens after it's redeemed.
        record.ConsumedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var contact = await dbContext.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == record.ContactId, cancellationToken);
        if (contact is null || contact.TrashedAt is not null)
        {
            return null;
        }

        return new ClientPortalContactView(contact.Id, contact.FullName, contact.Email ?? string.Empty);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
