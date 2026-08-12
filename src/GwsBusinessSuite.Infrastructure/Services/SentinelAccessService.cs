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
                item.RevokedAt != null, item.PasswordHash != null, item.ViewCount, item.LastAccessedAt, item.IsAutomationWorkflow))
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

    public async Task<SentinelShareView> CreatePublicShareAsync(
        Guid targetId, bool isDatabase, DateTimeOffset? expiresAt, bool allowSearchIndexing, string? password, string performedBy,
        bool isAutomationWorkflow = false, CancellationToken cancellationToken = default)
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
            IsAutomationWorkflow = isAutomationWorkflow,
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
        return new SentinelShareView(entity.Id, targetId, isDatabase, token, expiresAt, allowSearchIndexing, false, hasPassword, IsAutomationWorkflow: isAutomationWorkflow);
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
        return new SentinelShareView(item.Id, item.TargetId, item.IsDatabase, null, item.ExpiresAt, item.AllowSearchIndexing, false, item.PasswordHash != null, IsAutomationWorkflow: item.IsAutomationWorkflow);
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
        var target = new SentinelAccessTarget(targetId, isDatabase);
        var accessibleTargets = await GetAccessibleTargetsAsync(
            [target],
            username,
            requiredAccessLevel,
            cancellationToken);
        return accessibleTargets.Contains(target);
    }

    public async Task<bool> HasDirectPermissionAsync(Guid targetId, bool isDatabase, string username, string requiredAccessLevel, CancellationToken cancellationToken = default)
    {
        if (!AccessRanks.TryGetValue(requiredAccessLevel, out var requiredRank)) return false;
        if (await IsOwnerOrAdminAsync(username, cancellationToken)) return true;

        var accessLevel = await dbContext.SentinelResourcePermissions.AsNoTracking()
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase && item.Username == username)
            .Select(item => item.AccessLevel)
            .FirstOrDefaultAsync(cancellationToken);
        return accessLevel is not null && AccessRanks.GetValueOrDefault(accessLevel, -1) >= requiredRank;
    }

    // Two independent "has full access to everything" overrides: a SentinelWorkspaceMembers
    // Owner row (per-workspace concept, populated by the Sentinel-specific membership flow), and
    // an AppUsers.Role == Admin account (the app-wide role every other admin-only surface - e.g.
    // Wiki.razor's `_isAdmin ||` gate - already trusts). Without the second check here, an Admin
    // who was never separately added as a SentinelWorkspaceMembers Owner gets denied by every
    // access-gated action that doesn't have its own redundant admin bypass (this was the root
    // cause of "Unable to update favorite: you don't have access" for an Admin user).
    private async Task<bool> IsOwnerOrAdminAsync(string username, CancellationToken cancellationToken) =>
        await dbContext.SentinelWorkspaceMembers.AsNoTracking()
            .AnyAsync(item => item.Username == username && item.Role == SentinelWorkspaceRoles.Owner, cancellationToken)
        || await dbContext.AppUsers.AsNoTracking()
            .AnyAsync(item => item.Username == username && item.Role == AppRoles.Admin, cancellationToken);

    public async Task<IReadOnlySet<SentinelAccessTarget>> GetAccessibleTargetsAsync(
        IReadOnlyCollection<SentinelAccessTarget> targets,
        string username,
        string requiredAccessLevel,
        CancellationToken cancellationToken = default)
    {
        if (!AccessRanks.TryGetValue(requiredAccessLevel, out var requiredRank) || targets.Count == 0)
        {
            return new HashSet<SentinelAccessTarget>();
        }

        var distinctTargets = targets.ToHashSet();
        if (await IsOwnerOrAdminAsync(username, cancellationToken))
        {
            // Preserve the existing owner override: owners have full access independently of
            // resource-specific permissions (including during create/delete transition windows).
            return distinctTargets;
        }

        // The batch API is intended for full workspace tree/search filtering. Loading these small
        // projections once avoids an N+1 query per target and gives the resolver enough information
        // to validate the entire ancestry chain before trusting an inherited permission.
        var pageNodes = await dbContext.WikiPages.AsNoTracking()
            .Select(item => new { item.Id, item.ParentWikiPageId })
            .ToListAsync(cancellationToken);
        var databaseNodes = await dbContext.WikiDatabases.AsNoTracking()
            .Select(item => new { item.Id, item.ParentWikiPageId })
            .ToListAsync(cancellationToken);
        var permissionRows = await dbContext.SentinelResourcePermissions.AsNoTracking()
            .Where(item => item.Username == username)
            .Select(item => new { item.TargetId, item.IsDatabase, item.AccessLevel })
            .ToListAsync(cancellationToken);

        var pageParents = pageNodes.ToDictionary(item => item.Id, item => item.ParentWikiPageId);
        var databaseParents = databaseNodes.ToDictionary(item => item.Id, item => item.ParentWikiPageId);
        var accessByTarget = permissionRows.ToDictionary(
            item => new SentinelAccessTarget(item.TargetId, item.IsDatabase),
            item => item.AccessLevel);

        var accessibleTargets = new HashSet<SentinelAccessTarget>();
        foreach (var target in distinctTargets)
        {
            var effectiveAccess = ResolveEffectiveAccess(target, pageParents, databaseParents, accessByTarget);
            if (effectiveAccess is not null
                && AccessRanks.GetValueOrDefault(effectiveAccess, -1) >= requiredRank)
            {
                accessibleTargets.Add(target);
            }
        }

        return accessibleTargets;
    }

    private static string? ResolveEffectiveAccess(
        SentinelAccessTarget target,
        IReadOnlyDictionary<Guid, Guid?> pageParents,
        IReadOnlyDictionary<Guid, Guid?> databaseParents,
        IReadOnlyDictionary<SentinelAccessTarget, string> accessByTarget)
    {
        Guid? parentPageId;
        var visitedPageIds = new HashSet<Guid>();
        if (target.IsDatabase)
        {
            if (!databaseParents.TryGetValue(target.TargetId, out parentPageId))
            {
                return null;
            }
        }
        else
        {
            if (!pageParents.TryGetValue(target.TargetId, out parentPageId))
            {
                return null;
            }

            // Including the target detects a parent chain that loops back to the child itself.
            visitedPageIds.Add(target.TargetId);
        }

        // Direct target permission is authoritative. In particular, an explicit view grant on a
        // child narrows an inherited edit/full-access grant from its parent.
        if (accessByTarget.TryGetValue(target, out var directAccess))
        {
            return directAccess;
        }

        var ancestorPageIds = new List<Guid>();
        while (parentPageId is { } currentPageId)
        {
            if (!visitedPageIds.Add(currentPageId)
                || !pageParents.TryGetValue(currentPageId, out parentPageId))
            {
                // Do not accept a grant encountered before discovering malformed ancestry. The
                // whole inheritance path must terminate at a real root before it is trusted.
                return null;
            }

            ancestorPageIds.Add(currentPageId);
        }

        foreach (var ancestorPageId in ancestorPageIds)
        {
            if (accessByTarget.TryGetValue(new SentinelAccessTarget(ancestorPageId, IsDatabase: false), out var inheritedAccess))
            {
                // Nearest explicit ancestor wins, including a weaker level than a more distant
                // ancestor. Unknown/corrupt levels are returned and subsequently rank as denied.
                return inheritedAccess;
            }
        }

        return null;
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
