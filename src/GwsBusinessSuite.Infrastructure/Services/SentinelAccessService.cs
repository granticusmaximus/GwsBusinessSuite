using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelAccessService(IAppDbContext dbContext) : ISentinelAccessService
{
    private static readonly IReadOnlyDictionary<string, int> AccessRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [SentinelAccessLevels.View] = 0,
        [SentinelAccessLevels.Comment] = 1,
        [SentinelAccessLevels.Edit] = 2,
        [SentinelAccessLevels.FullAccess] = 3
    };

    public async Task<SentinelAccessSnapshot> GetAccessAsync(Guid targetId, bool isDatabase, CancellationToken cancellationToken = default)
    {
        var permissions = await dbContext.SentinelResourcePermissions.AsNoTracking()
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .OrderBy(item => item.Username)
            .Select(item => new SentinelPermissionView(item.Id, item.Username, item.AccessLevel))
            .ToListAsync(cancellationToken);
        var shares = await dbContext.SentinelPublicShares.AsNoTracking()
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .Select(item => new SentinelShareView(
                item.Id, item.TargetId, item.IsDatabase, null, item.ExpiresAt, item.AllowSearchIndexing,
                item.RevokedAt != null, item.PasswordHash != null, item.ViewCount, item.LastAccessedAt))
            .ToListAsync(cancellationToken);
        // SQLite stores DateTimeOffset as TEXT and cannot translate ORDER BY for it. Keep the
        // bounded resource-share query server-side, then order the already-materialized rows
        // using the source entities' creation order.
        var shareOrder = await dbContext.SentinelPublicShares.AsNoTracking()
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .Select(item => new { item.Id, item.CreatedAt })
            .ToListAsync(cancellationToken);
        var orderById = shareOrder.ToDictionary(item => item.Id, item => item.CreatedAt);
        return new SentinelAccessSnapshot(
            permissions,
            shares.OrderByDescending(share => orderById[share.Id]).ToList());
    }

    public async Task SetPermissionAsync(Guid targetId, bool isDatabase, string username, string accessLevel, string performedBy, CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (username.Length == 0) throw new ArgumentException("Username is required.", nameof(username));
        if (!AccessRanks.ContainsKey(accessLevel)) throw new ArgumentException("Unknown access level.", nameof(accessLevel));
        var permission = await dbContext.SentinelResourcePermissions
            .FirstOrDefaultAsync(item => item.TargetId == targetId && item.IsDatabase == isDatabase && item.Username == username, cancellationToken);
        if (permission is null)
        {
            permission = new SentinelResourcePermission
            {
                TargetId = targetId, IsDatabase = isDatabase, Username = username,
                CreatedAt = DateTimeOffset.UtcNow, CreatedBy = performedBy
            };
            await dbContext.SentinelResourcePermissions.AddAsync(permission, cancellationToken);
        }
        permission.AccessLevel = accessLevel;
        permission.UpdatedAt = DateTimeOffset.UtcNow;
        permission.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemovePermissionAsync(Guid permissionId, string performedBy, CancellationToken cancellationToken = default)
    {
        var permission = await dbContext.SentinelResourcePermissions.FirstOrDefaultAsync(item => item.Id == permissionId, cancellationToken)
            ?? throw new InvalidOperationException("Permission not found.");
        dbContext.SentinelResourcePermissions.Remove(permission);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SentinelShareView> CreatePublicShareAsync(Guid targetId, bool isDatabase, DateTimeOffset? expiresAt, bool allowSearchIndexing, string? password, string performedBy, CancellationToken cancellationToken = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hasPassword = !string.IsNullOrWhiteSpace(password);
        string? passwordSalt = null;
        string? passwordHash = null;
        if (hasPassword)
        {
            passwordSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            passwordHash = HashPassword(password!, passwordSalt);
        }
        var entity = new SentinelPublicShare
        {
            TargetId = targetId,
            IsDatabase = isDatabase,
            TokenHash = HashToken(token),
            ExpiresAt = expiresAt,
            AllowSearchIndexing = allowSearchIndexing,
            PasswordSalt = passwordSalt,
            PasswordHash = passwordHash,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = performedBy
        };
        await dbContext.SentinelPublicShares.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SentinelShareView(entity.Id, targetId, isDatabase, token, expiresAt, allowSearchIndexing, false, hasPassword);
    }

    public async Task RevokePublicShareAsync(Guid shareId, string performedBy, CancellationToken cancellationToken = default)
    {
        var share = await dbContext.SentinelPublicShares.FirstOrDefaultAsync(item => item.Id == shareId, cancellationToken)
            ?? throw new InvalidOperationException("Public share not found.");
        share.RevokedAt = DateTimeOffset.UtcNow;
        share.UpdatedAt = DateTimeOffset.UtcNow;
        share.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SentinelShareView?> ResolvePublicShareAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token);
        var item = await dbContext.SentinelPublicShares.AsNoTracking()
            .FirstOrDefaultAsync(item => item.TokenHash == hash && item.RevokedAt == null, cancellationToken);
        if (item is null || item.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow) return null;
        return new SentinelShareView(item.Id, item.TargetId, item.IsDatabase, null, item.ExpiresAt, item.AllowSearchIndexing, false, item.PasswordHash != null);
    }

    public async Task<bool> VerifySharePasswordAsync(Guid shareId, string password, CancellationToken cancellationToken = default)
    {
        var share = await dbContext.SentinelPublicShares.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == shareId && item.RevokedAt == null, cancellationToken);
        if (share?.PasswordHash is null || share.PasswordSalt is null) return false;
        return HashPassword(password, share.PasswordSalt) == share.PasswordHash;
    }

    public async Task RecordShareViewAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        var share = await dbContext.SentinelPublicShares.FirstOrDefaultAsync(item => item.Id == shareId, cancellationToken);
        if (share is null) return;
        share.ViewCount++;
        share.LastAccessedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CanAccessAsync(Guid targetId, bool isDatabase, string username, string requiredAccessLevel, CancellationToken cancellationToken = default)
    {
        if (!AccessRanks.TryGetValue(requiredAccessLevel, out var requiredRank)) return false;
        var member = await dbContext.SentinelWorkspaceMembers.AsNoTracking().FirstOrDefaultAsync(item => item.Username == username, cancellationToken);
        if (member?.Role == SentinelWorkspaceRoles.Owner) return true;
        var access = await dbContext.SentinelResourcePermissions.AsNoTracking()
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase && item.Username == username)
            .Select(item => item.AccessLevel)
            .FirstOrDefaultAsync(cancellationToken);
        return access is not null && AccessRanks.GetValueOrDefault(access, -1) >= requiredRank;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string HashPassword(string password, string saltHex) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saltHex + password))).ToLowerInvariant();
}
