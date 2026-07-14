namespace GwsBusinessSuite.Application.Users;

// Previously the only protection against password-guessing on /auth/login was the
// global IP-based rate limiter (Program.cs's "login" policy) - which, before
// UseForwardedHeaders was fixed, was effectively a single shared bucket for every
// client behind the proxy, and even correctly IP-scoped doesn't stop a distributed
// attempt against one specific account. This adds a second, per-account layer on top.
public static class LoginLockoutPolicy
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
}

public sealed record LoginAttemptResult(
    bool Succeeded,
    UserView? User,
    bool IsLockedOut,
    TimeSpan? LockoutRemaining);
