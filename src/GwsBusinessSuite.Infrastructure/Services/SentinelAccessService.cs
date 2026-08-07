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
        var share = await dbContext.SentinelPublicShares
            .FirstOrDefaultAsync(item => item.Id == shareId && item.RevokedAt == null, cancellationToken);
        if (share?.PasswordHash is null || share.PasswordSalt is null) return false;

        var now = DateTimeOffset.UtcNow;
        if (share.PasswordLockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return false;
        }

        var isCorrect = VerifyPassword(password, share.PasswordSalt, share.PasswordHash);
        if (isCorrect)
        {
            share.FailedPasswordAttempts = 0;
            share.PasswordLockedUntil = null;
        }
        else
        {
            share.FailedPasswordAttempts++;
            if (share.FailedPasswordAttempts >= MaxFailedPasswordAttempts)
            {
                share.PasswordLockedUntil = now.Add(PasswordLockoutDuration);
                share.FailedPasswordAttempts = 0;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return isCorrect;
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

    private const int MaxFailedPasswordAttempts = 5;
    private static readonly TimeSpan PasswordLockoutDuration = TimeSpan.FromMinutes(15);
    private const int Pbkdf2Iterations = 210_000;
    private const int Pbkdf2HashLengthBytes = 32;
    private const string Pbkdf2Prefix = "pbkdf2:";

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // Every new share password is stretched with PBKDF2-SHA256 (unlike TokenHash above, which
    // hashes an already-high-entropy random token and doesn't need stretching). A share
    // password is user-chosen and low-entropy, so the previous bare SHA-256(salt+password) was
    // crackable at GPU speed once a hash leaked.
    private static string HashPassword(string password, string saltHex)
    {
        var salt = Convert.FromHexString(saltHex);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, Pbkdf2HashLengthBytes);
        return $"{Pbkdf2Prefix}{Pbkdf2Iterations}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    // Shares created before this fix stored a bare SHA-256(salt+password) hex digest with no
    // prefix. Verify against that legacy format too so existing share links don't suddenly stop
    // working; HashPassword above always writes the PBKDF2 format for every new/changed share.
    private static bool VerifyPassword(string password, string saltHex, string storedHash)
    {
        if (storedHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
        {
            var parts = storedHash[Pbkdf2Prefix.Length..].Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var iterations))
            {
                return false;
            }

            var salt = Convert.FromHexString(saltHex);
            var computed = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, Pbkdf2HashLengthBytes);
            return CryptographicOperations.FixedTimeEquals(computed, Convert.FromHexString(parts[1]));
        }

        var legacyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saltHex + password))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(legacyHash), Encoding.UTF8.GetBytes(storedHash));
    }
}
