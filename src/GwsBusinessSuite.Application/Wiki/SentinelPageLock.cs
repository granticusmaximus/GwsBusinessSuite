using System.Text.Json;

namespace GwsBusinessSuite.Application.Wiki;

// Page lock (.claude/plans/sentinel-page-lock-2026-10.md). A locked page's content is kept only
// encrypted (WikiPage.LockedContentProtected; BlocksJson "[]", Markdown empty), so anything that
// reads pages without going through ISentinelPageLockService sees an empty page - the lock fails
// closed. Only the person who locked a page can read it, after unlocking with Touch ID (passkey or
// the Mac app) or their PIN; one unlock covers all their locked pages for 15 minutes in that
// sign-in session ("sid" claim). Saving a locked page needs that same unlock (WikiService checks).
public static class SentinelPageLock
{
    public static readonly TimeSpan UnlockWindow = TimeSpan.FromMinutes(15);
    public const int MaxPinFailures = 5;
    public static readonly TimeSpan PinFailureWindow = TimeSpan.FromMinutes(15);
    public const string SessionClaim = "sid";

    public static class Methods
    {
        public const string Pin = "pin";
        public const string Passkey = "passkey";
        public const string Device = "device";
    }

    // 6-8 digits.
    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= 6 and <= 8 } && pin.All(char.IsAsciiDigit);

    private sealed record Payload(string BlocksJson, string Markdown);

    public static string Pack(string blocksJson, string markdown) =>
        JsonSerializer.Serialize(new Payload(blocksJson, markdown));

    public static (string BlocksJson, string Markdown) Unpack(string json)
    {
        var payload = JsonSerializer.Deserialize<Payload>(json) ?? new Payload("[]", string.Empty);
        return (payload.BlocksJson, payload.Markdown);
    }
}

// Encrypts locked content (ASP.NET Data Protection in production).
public interface ISentinelPageLockCrypto
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

// Thrown when something tries to save a locked page without an unlock.
public sealed class SentinelPageLockedException(string message) : InvalidOperationException(message);

// A message for the person (wrong PIN, nothing set up yet, ...).
public sealed class SentinelLockException(string message) : Exception(message);

public sealed record SentinelLockStatus(
    bool IsLocked,
    string? LockedBy,
    long? LockedAtUnix,
    // The requester is the person who locked it, so they can unlock it.
    bool IsOwner,
    // The requester can read it right now (owner with a live unlock).
    bool IsUnlocked,
    long? UnlockExpiresAtUnix);

public sealed record SentinelUnlockMethodsView(bool HasPin, int Passkeys, int DeviceKeys)
{
    public bool Any => HasPin || Passkeys > 0 || DeviceKeys > 0;
}

public interface ISentinelPageLockService
{
    Task<SentinelLockStatus> GetStatusAsync(Guid pageId, string username, string? sessionId, CancellationToken cancellationToken = default);
    Task<SentinelUnlockMethodsView> GetMethodsAsync(string username, CancellationToken cancellationToken = default);

    // Locks the page and its sub-pages (that aren't locked by someone else). Needs Edit and an
    // unlock method set up first, so the person can get back in.
    Task<int> LockAsync(Guid pageId, string username, CancellationToken cancellationToken = default);
    // Owner, while unlocked: takes the lock off for good (content stored normally again).
    Task RemoveLockAsync(Guid pageId, string username, string? sessionId, CancellationToken cancellationToken = default);
    // Admin break-glass: removes someone else's lock (audited; the owner is notified).
    Task AdminRemoveLockAsync(Guid pageId, string adminUsername, CancellationToken cancellationToken = default);

    // The page's real blocks for its owner with a live unlock; null otherwise.
    Task<string?> ReadBlocksAsync(Guid pageId, string username, string? sessionId, CancellationToken cancellationToken = default);
    Task<bool> IsUnlockedAsync(string username, string? sessionId, CancellationToken cancellationToken = default);
    Task<long> UnlockWithPinAsync(string username, string? sessionId, string pin, string? ipAddress, CancellationToken cancellationToken = default);
    // For the passkey / Mac app paths, after they've verified the person themselves.
    Task<long> GrantAsync(string username, string? sessionId, string method, CancellationToken cancellationToken = default);
    // Ends every unlock this person has (all sessions).
    Task LockAllNowAsync(string username, CancellationToken cancellationToken = default);
    // Ends the unlock of one sign-in session (the Mac app being hidden).
    Task LockSessionNowAsync(string username, string? sessionId, CancellationToken cancellationToken = default);

    // Changing a PIN needs the current one; a first PIN, when Touch ID is already set up, needs a
    // live unlock in this session (so a stolen session can't add its own way in).
    Task SetPinAsync(string username, string? sessionId, string? currentPin, string newPin, CancellationToken cancellationToken = default);
    // Throws unless adding another unlock method is allowed now: either none exists yet, or this
    // session is unlocked.
    Task RequireCanAddMethodAsync(string username, string? sessionId, CancellationToken cancellationToken = default);
}
