using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Page-lock encryption: ASP.NET Data Protection under its own purpose, so these keys can't be
// used to read any other protected value and vice versa.
public sealed class DataProtectionPageLockCrypto(IDataProtectionProvider provider) : ISentinelPageLockCrypto
{
    private readonly IDataProtector _protector = provider.CreateProtector("GwsBusinessSuite.Sentinel.PageLock.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

// Page lock (SentinelPageLock). Locking moves a page's content (and its version history and
// suggested edits) into encrypted storage and out of everything readable without the unlock:
// the search index, #tags, the live copy in BlocksJson.
public sealed class SentinelPageLockService(
    IAppDbContext db,
    ISentinelAccessService accessService,
    ISentinelPageLockCrypto crypto,
    TimeProvider timeProvider,
    ISecurityAuditService? securityAudit = null) : ISentinelPageLockService
{
    private const string LockedSuggestionPrefix = "locked:";
    private static readonly PasswordHasher<string> PinHasher = new();

    public async Task<SentinelLockStatus> GetStatusAsync(Guid pageId, string username, string? sessionId, CancellationToken cancellationToken = default)
    {
        var page = await db.WikiPages.AsNoTracking()
            .Where(item => item.Id == pageId)
            .Select(item => new { item.LockedBy, item.LockedAtUnix })
            .FirstOrDefaultAsync(cancellationToken);
        if (page?.LockedBy is null) return new SentinelLockStatus(false, null, null, false, false, null);
        var isOwner = string.Equals(page.LockedBy, username, StringComparison.OrdinalIgnoreCase);
        var expires = isOwner ? await ActiveGrantExpiryAsync(username, sessionId, cancellationToken) : null;
        return new SentinelLockStatus(true, page.LockedBy, page.LockedAtUnix, isOwner, expires is not null, expires);
    }

    public async Task<SentinelUnlockMethodsView> GetMethodsAsync(string username, CancellationToken cancellationToken = default) =>
        new(await db.SentinelLockPins.AnyAsync(pin => pin.Username == username, cancellationToken),
            await db.SentinelPasskeys.CountAsync(key => key.Username == username, cancellationToken),
            await db.SentinelDeviceKeys.CountAsync(key => key.Username == username && key.RevokedAtUnix == null, cancellationToken));

    public async Task<int> LockAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        var root = await db.WikiPages.FirstOrDefaultAsync(page => page.Id == pageId && page.TrashedAt == null, cancellationToken)
            ?? throw new SentinelLockException("That page no longer exists.");
        if (root.LockedBy is not null) throw new SentinelLockException("This page is already locked.");
        if (!await accessService.CanAccessAsync(pageId, false, username, SentinelAccessLevels.Edit, cancellationToken))
            throw new SentinelLockException("You need edit access to lock this page.");
        if (!(await GetMethodsAsync(username, cancellationToken)).Any)
            throw new SentinelLockException("Set up a PIN or Touch ID first (Lock settings), so you can open the page again.");

        // The page and the sub-pages under it that aren't locked yet and that this person can edit.
        var all = await db.WikiPages.Where(page => page.TrashedAt == null).ToListAsync(cancellationToken);
        var toLock = new List<WikiPage> { root };
        var queue = new Queue<Guid>([root.Id]);
        var seen = new HashSet<Guid> { root.Id };
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var child in all.Where(page => page.ParentWikiPageId == parentId && seen.Add(page.Id)))
            {
                queue.Enqueue(child.Id);
                if (child.LockedBy is null && await accessService.CanAccessAsync(child.Id, false, username, SentinelAccessLevels.Edit, cancellationToken))
                    toLock.Add(child);
            }
        }

        var now = timeProvider.GetUtcNow();
        var ids = toLock.Select(page => page.Id).ToList();
        foreach (var page in toLock)
        {
            page.LockedContentProtected = crypto.Protect(SentinelPageLock.Pack(page.BlocksJson, page.Markdown));
            page.BlocksJson = "[]";
            page.Markdown = string.Empty;
            page.LockedBy = username;
            page.LockedAtUnix = now.ToUnixTimeSeconds();
            page.ContentVersion++;
            page.UpdatedAt = now;
            page.UpdatedBy = username;
        }
        foreach (var revision in await db.WikiPageRevisions.Where(revision => ids.Contains(revision.WikiPageId) && revision.ContentProtected == null).ToListAsync(cancellationToken))
        {
            revision.ContentProtected = crypto.Protect(SentinelPageLock.Pack(revision.BlocksJson, string.Empty));
            revision.BlocksJson = "[]";
        }
        foreach (var suggestion in await db.PageSuggestions.Where(item => ids.Contains(item.WikiPageId)).ToListAsync(cancellationToken))
        {
            if (!suggestion.ChangesJson.StartsWith(LockedSuggestionPrefix, StringComparison.Ordinal))
                suggestion.ChangesJson = LockedSuggestionPrefix + crypto.Protect(suggestion.ChangesJson);
        }
        // Words that came from the text: #tags written in it and its search-index entry.
        db.PageTags.RemoveRange(await db.PageTags.Where(tag => ids.Contains(tag.WikiPageId) && !tag.IsManual).ToListAsync(cancellationToken));
        db.SemanticSearchDocuments.RemoveRange(await db.SemanticSearchDocuments
            .Where(document => document.SourceType == SemanticSourceTypes.WikiPage && ids.Contains(document.SourceId)).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("SentinelPageLocked", SecurityAuditOutcomes.Succeeded, username, pageId, $"{toLock.Count} page(s)", cancellationToken);
        return toLock.Count;
    }

    public async Task RemoveLockAsync(Guid pageId, string username, string? sessionId, CancellationToken cancellationToken = default)
    {
        var page = await db.WikiPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken)
            ?? throw new SentinelLockException("That page no longer exists.");
        if (page.LockedBy is null) return;
        if (!string.Equals(page.LockedBy, username, StringComparison.OrdinalIgnoreCase))
            throw new SentinelLockException("Only the person who locked this page can remove its lock.");
        if (await ActiveGrantExpiryAsync(username, sessionId, cancellationToken) is null)
            throw new SentinelLockException("Unlock the page first.");
        await UnlockStorageAsync(page, username, cancellationToken);
        await AuditAsync("SentinelPageLockRemoved", SecurityAuditOutcomes.Succeeded, username, pageId, null, cancellationToken);
    }

    public async Task AdminRemoveLockAsync(Guid pageId, string adminUsername, CancellationToken cancellationToken = default)
    {
        if (!await db.AppUsers.AnyAsync(user => user.Username == adminUsername && user.Role == AppRoles.Admin && user.IsActive, cancellationToken))
            throw new SentinelLockException("Only an admin can remove someone else's lock.");
        var page = await db.WikiPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken)
            ?? throw new SentinelLockException("That page no longer exists.");
        if (page.LockedBy is not { } owner) return;
        await UnlockStorageAsync(page, adminUsername, cancellationToken);
        if (!string.Equals(owner, adminUsername, StringComparison.OrdinalIgnoreCase))
        {
            db.SentinelNotifications.Add(new SentinelNotification
            {
                Username = owner,
                Kind = "lock-removed",
                WikiPageId = page.Id,
                Message = $"{adminUsername} removed the lock from \"{(string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title)}\".",
                CreatedBy = adminUsername
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        await AuditAsync("SentinelPageLockRemovedByAdmin", SecurityAuditOutcomes.Succeeded, adminUsername, pageId, $"owner {owner}", cancellationToken, SecurityAuditSeverities.High);
    }

    public async Task<string?> ReadBlocksAsync(Guid pageId, string username, string? sessionId, CancellationToken cancellationToken = default)
    {
        var page = await db.WikiPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null) return null;
        if (page.LockedBy is null) return page.BlocksJson;
        if (!string.Equals(page.LockedBy, username, StringComparison.OrdinalIgnoreCase) || page.LockedContentProtected is null) return null;
        if (await ActiveGrantExpiryAsync(username, sessionId, cancellationToken) is null) return null;
        return SentinelPageLock.Unpack(crypto.Unprotect(page.LockedContentProtected)).BlocksJson;
    }

    public async Task<bool> IsUnlockedAsync(string username, string? sessionId, CancellationToken cancellationToken = default) =>
        await ActiveGrantExpiryAsync(username, sessionId, cancellationToken) is not null;

    public async Task<long> UnlockWithPinAsync(string username, string? sessionId, string pin, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await VerifyPinAsync(username, pin, cancellationToken);
        return await GrantAsync(username, sessionId, SentinelPageLock.Methods.Pin, cancellationToken);
    }

    public async Task<long> GrantAsync(string username, string? sessionId, string method, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new SentinelLockException("Sign out and sign in again to use locked pages (this sign-in predates page locks).");
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        db.SentinelUnlockGrants.RemoveRange(await db.SentinelUnlockGrants
            .Where(grant => grant.Username == username && (grant.ExpiresAtUnix <= now || grant.SessionId == sessionId)).ToListAsync(cancellationToken));
        var expires = now + (long)SentinelPageLock.UnlockWindow.TotalSeconds;
        db.SentinelUnlockGrants.Add(new SentinelUnlockGrant
        {
            Username = username, SessionId = sessionId, Method = method, CreatedAtUnix = now, ExpiresAtUnix = expires, CreatedBy = username
        });
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("SentinelPagesUnlocked", SecurityAuditOutcomes.Succeeded, username, null, method, cancellationToken);
        return expires;
    }

    public async Task LockAllNowAsync(string username, CancellationToken cancellationToken = default)
    {
        db.SentinelUnlockGrants.RemoveRange(await db.SentinelUnlockGrants.Where(grant => grant.Username == username).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RequireCanAddMethodAsync(string username, string? sessionId, CancellationToken cancellationToken = default)
    {
        if ((await GetMethodsAsync(username, cancellationToken)).Any && await ActiveGrantExpiryAsync(username, sessionId, cancellationToken) is null)
            throw new SentinelLockException("Unlock first (PIN or Touch ID) - then you can add another way to unlock.");
    }

    public async Task LockSessionNowAsync(string username, string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        db.SentinelUnlockGrants.RemoveRange(await db.SentinelUnlockGrants
            .Where(grant => grant.Username == username && grant.SessionId == sessionId).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPinAsync(string username, string? sessionId, string? currentPin, string newPin, CancellationToken cancellationToken = default)
    {
        if (!SentinelPageLock.IsValidPin(newPin)) throw new SentinelLockException("A PIN is 6 to 8 digits.");
        var existing = await db.SentinelLockPins.FirstOrDefaultAsync(pin => pin.Username == username, cancellationToken);
        if (existing is null) await RequireCanAddMethodAsync(username, sessionId, cancellationToken);
        if (existing is not null)
        {
            // Changing a PIN needs the current one (with the same attempt limit as unlocking).
            await VerifyPinAsync(username, currentPin ?? string.Empty, cancellationToken);
            existing.PinHash = PinHasher.HashPassword(username, newPin);
            existing.UpdatedAt = timeProvider.GetUtcNow();
            existing.UpdatedBy = username;
        }
        else
        {
            db.SentinelLockPins.Add(new SentinelLockPin { Username = username, PinHash = PinHasher.HashPassword(username, newPin), CreatedBy = username });
        }
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("SentinelLockPinSet", SecurityAuditOutcomes.Succeeded, username, null, existing is null ? "created" : "changed", cancellationToken);
    }

    private async Task VerifyPinAsync(string username, string pin, CancellationToken cancellationToken)
    {
        var record = await db.SentinelLockPins.FirstOrDefaultAsync(item => item.Username == username, cancellationToken)
            ?? throw new SentinelLockException("You haven't set a PIN yet.");
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (record.LockedUntilUnix is { } lockedUntil && lockedUntil > now)
            throw new SentinelLockException($"Too many wrong PINs - try again at {DateTimeOffset.FromUnixTimeSeconds(lockedUntil).ToLocalTime().ToGwsTime()} or use Touch ID.");
        var ok = SentinelPageLock.IsValidPin(pin)
            && PinHasher.VerifyHashedPassword(username, record.PinHash, pin) != PasswordVerificationResult.Failed;
        if (ok)
        {
            record.FailedCount = 0;
            record.FailureWindowStartUnix = null;
            record.LockedUntilUnix = null;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var window = (long)SentinelPageLock.PinFailureWindow.TotalSeconds;
        if (record.FailureWindowStartUnix is not { } start || now - start > window)
        {
            record.FailureWindowStartUnix = now;
            record.FailedCount = 0;
        }
        record.FailedCount++;
        if (record.FailedCount >= SentinelPageLock.MaxPinFailures) record.LockedUntilUnix = now + window;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("SentinelLockPinFailed", SecurityAuditOutcomes.Failed, username, null, $"attempt {record.FailedCount}", cancellationToken, SecurityAuditSeverities.Warning);
        var left = SentinelPageLock.MaxPinFailures - record.FailedCount;
        throw new SentinelLockException(left > 0
            ? $"Wrong PIN - {left} {(left == 1 ? "try" : "tries")} left."
            : "Wrong PIN - PIN unlock is paused for 15 minutes. Use Touch ID, or try again later.");
    }

    private async Task<long?> ActiveGrantExpiryAsync(string username, string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var expiries = await db.SentinelUnlockGrants.AsNoTracking()
            .Where(grant => grant.Username == username && grant.SessionId == sessionId && grant.ExpiresAtUnix > now)
            .Select(grant => grant.ExpiresAtUnix)
            .ToListAsync(cancellationToken);
        return expiries.Count == 0 ? null : expiries.Max();
    }

    private async Task UnlockStorageAsync(WikiPage page, string actor, CancellationToken cancellationToken)
    {
        if (page.LockedContentProtected is not null)
        {
            var (blocks, markdown) = SentinelPageLock.Unpack(crypto.Unprotect(page.LockedContentProtected));
            page.BlocksJson = blocks;
            page.Markdown = markdown;
        }
        page.LockedContentProtected = null;
        page.LockedBy = null;
        page.LockedAtUnix = null;
        page.ContentVersion++;
        page.UpdatedAt = timeProvider.GetUtcNow();
        page.UpdatedBy = actor;
        foreach (var revision in await db.WikiPageRevisions.Where(revision => revision.WikiPageId == page.Id && revision.ContentProtected != null).ToListAsync(cancellationToken))
        {
            revision.BlocksJson = SentinelPageLock.Unpack(crypto.Unprotect(revision.ContentProtected!)).BlocksJson;
            revision.ContentProtected = null;
        }
        foreach (var suggestion in await db.PageSuggestions.Where(item => item.WikiPageId == page.Id).ToListAsync(cancellationToken))
        {
            if (suggestion.ChangesJson.StartsWith(LockedSuggestionPrefix, StringComparison.Ordinal))
                suggestion.ChangesJson = crypto.Unprotect(suggestion.ChangesJson[LockedSuggestionPrefix.Length..]);
        }
        await db.SaveChangesAsync(cancellationToken);
        // #tags written in the text come back.
        await SentinelInsightsService.SyncTextTagsAsync(db, page.Id, page.BlocksJson, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AuditAsync(string action, string outcome, string username, Guid? pageId, string? detail, CancellationToken cancellationToken, string? severity = null)
    {
        if (securityAudit is null) return;
        await securityAudit.RecordAsync(new SecurityAuditInput(
            SecurityAuditCategories.Authentication, action, outcome,
            Severity: severity ?? SecurityAuditSeverities.Information,
            TargetType: pageId is null ? "AppUser" : "WikiPage",
            TargetId: pageId?.ToString() ?? username,
            Details: detail is null ? null : new Dictionary<string, string?> { ["detail"] = detail },
            ActorUsername: username), cancellationToken);
    }
}
