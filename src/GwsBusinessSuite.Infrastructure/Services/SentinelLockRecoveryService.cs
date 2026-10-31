using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Infrastructure.Services;

// ISentinelLockRecoveryService: the authenticator code as a way to unlock. VerifyMfaAsync refuses a
// replayed code and takes a recovery code once. Wrong codes are counted here per person (the
// Blazor panel isn't behind the HTTP rate limits): five in 15 minutes pauses it, like the PIN.
public sealed class SentinelLockRecoveryService(
    ISentinelPageLockService pageLocks,
    IUserManagementService users,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ISecurityAuditService? securityAudit = null) : ISentinelLockRecoveryService
{
    private sealed record Failures(int Count, DateTimeOffset Until);

    public async Task<long> UnlockWithAuthenticatorAsync(string username, string? sessionId, string code, CancellationToken cancellationToken = default)
    {
        var key = "sentinel-lock-authenticator:" + username.ToLowerInvariant();
        var now = timeProvider.GetUtcNow();
        if (cache.TryGetValue(key, out Failures? failures) && failures is { Count: >= SentinelPageLock.MaxPinFailures } && failures.Until > now)
            throw new SentinelLockException($"Too many wrong codes - try again at {failures.Until.ToLocalTime().ToGwsTime()}.");

        var user = (await users.ListUsersAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase) && item.IsActive);
        if (user is null || !user.MfaEnabled)
            throw new SentinelLockException("Your account has no authenticator app set up, so it can't be used here.");

        var result = await users.VerifyMfaAsync(user.Id, code, cancellationToken);
        if (!result.Succeeded)
        {
            var count = failures is not null && failures.Until > now ? failures.Count + 1 : 1;
            var until = now + SentinelPageLock.PinFailureWindow;
            cache.Set(key, new Failures(count, until), until);
            await AuditAsync(SecurityAuditOutcomes.Failed, username, $"attempt {count}", SecurityAuditSeverities.Warning, cancellationToken);
            throw new SentinelLockException("That code isn't right - enter the current code from your authenticator app.");
        }
        cache.Remove(key);
        await AuditAsync(SecurityAuditOutcomes.Succeeded, username, result.UsedRecoveryCode ? "recovery code" : "authenticator code", null, cancellationToken);
        return await pageLocks.GrantAsync(username, sessionId, SentinelPageLock.Methods.Authenticator, cancellationToken);
    }

    private async Task AuditAsync(string outcome, string username, string detail, string? severity, CancellationToken cancellationToken)
    {
        if (securityAudit is null) return;
        await securityAudit.RecordAsync(new SecurityAuditInput(
            SecurityAuditCategories.Authentication, "SentinelLockAuthenticatorUnlock", outcome,
            Severity: severity ?? SecurityAuditSeverities.Information,
            TargetType: "AppUser", TargetId: username,
            Details: new Dictionary<string, string?> { ["detail"] = detail },
            ActorUsername: username), cancellationToken);
    }
}
