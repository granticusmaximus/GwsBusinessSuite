using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DeveloperApiKeyService(IAppDbContext db, TimeProvider timeProvider) : IDeveloperApiKeyService
{
    private const string KeyMarker = "gws_live_";

    public async Task<IReadOnlyList<DeveloperApiKeyView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.DeveloperApiKeys.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderByDescending(item => item.CreatedAt).Select(Map).ToList();
    }

    public async Task<IssuedDeveloperApiKey> IssueAsync(
        string name,
        IReadOnlyCollection<string> scopes,
        int rateLimitPerMinute,
        DateTimeOffset? expiresAt,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 1 or > 100)
        {
            throw new InvalidOperationException("Key name must be between 1 and 100 characters.");
        }

        var normalizedScopes = scopes
            .Select(scope => scope.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
        if (normalizedScopes.Length == 0 || normalizedScopes.Any(scope => !DeveloperApiScopes.All.Contains(scope)))
        {
            throw new InvalidOperationException("Select at least one valid API scope.");
        }
        if (rateLimitPerMinute is < 1 or > 600)
        {
            throw new InvalidOperationException("Rate limit must be between 1 and 600 requests per minute.");
        }

        var now = timeProvider.GetUtcNow();
        if (expiresAt is not null && expiresAt <= now)
        {
            throw new InvalidOperationException("Expiration must be in the future.");
        }

        var selector = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var plaintext = $"{KeyMarker}{selector}_{secret}";
        var row = new DeveloperApiKey
        {
            Name = normalizedName,
            KeyPrefix = $"{KeyMarker}{selector}",
            KeyHash = Hash(plaintext),
            ScopesCsv = string.Join(',', normalizedScopes),
            RateLimitPerMinute = rateLimitPerMinute,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            CreatedBy = performedBy
        };
        await db.DeveloperApiKeys.AddAsync(row, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedDeveloperApiKey(Map(row), plaintext);
    }

    public async Task RevokeAsync(Guid id, string performedBy, CancellationToken cancellationToken = default)
    {
        var row = await db.DeveloperApiKeys.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("API key was not found.");
        if (row.RevokedAt is null)
        {
            row.RevokedAt = timeProvider.GetUtcNow();
            row.RevokedBy = performedBy;
            row.UpdatedAt = row.RevokedAt;
            row.UpdatedBy = performedBy;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<AuthenticatedDeveloperApiKey?> AuthenticateAsync(
        string plaintextKey,
        CancellationToken cancellationToken = default)
    {
        var separator = plaintextKey.LastIndexOf('_');
        if (!plaintextKey.StartsWith(KeyMarker, StringComparison.Ordinal) || separator <= KeyMarker.Length)
        {
            return null;
        }

        var prefix = plaintextKey[..separator];
        var row = await db.DeveloperApiKeys.FirstOrDefaultAsync(item => item.KeyPrefix == prefix, cancellationToken);
        var suppliedHash = Encoding.ASCII.GetBytes(Hash(plaintextKey));
        if (row is null || row.RevokedAt is not null || row.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return null;
        }

        var storedHash = Encoding.ASCII.GetBytes(row.KeyHash);
        if (suppliedHash.Length != storedHash.Length || !CryptographicOperations.FixedTimeEquals(suppliedHash, storedHash))
        {
            return null;
        }

        row.LastUsedAt = timeProvider.GetUtcNow();
        row.RequestCount++;
        await db.SaveChangesAsync(cancellationToken);
        return new AuthenticatedDeveloperApiKey(
            row.Id,
            row.Name,
            ParseScopes(row.ScopesCsv),
            row.RateLimitPerMinute,
            row.CreatedBy);
    }

    private static DeveloperApiKeyView Map(DeveloperApiKey row) => new(
        row.Id,
        row.Name,
        row.KeyPrefix,
        ParseScopes(row.ScopesCsv),
        row.RateLimitPerMinute,
        row.CreatedAt,
        row.ExpiresAt,
        row.LastUsedAt,
        row.RequestCount,
        row.RevokedAt);

    private static string[] ParseScopes(string scopesCsv) => scopesCsv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
