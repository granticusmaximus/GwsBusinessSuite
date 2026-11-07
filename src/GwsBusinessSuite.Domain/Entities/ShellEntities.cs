using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// The admin rail's shortcuts (shell redesign R1b): pages a person pinned, and records they opened
// lately. Url is the app-relative address (path and query, no leading slash). LastUsedUnix orders
// them (pinned time for a pin, last visit for a recent) - a long, since SQLite can't sort
// DateTimeOffset.
public sealed class UserShortcut : AuditableEntity
{
    public required string Username { get; set; }
    public required string Kind { get; set; }
    public required string Url { get; set; }
    public required string Title { get; set; }
    public string Icon { get; set; } = string.Empty;
    public long LastUsedUnix { get; set; }
}

public static class UserShortcutKinds
{
    public const string Pin = "pin";
    public const string Recent = "recent";
}

// A named filter/column layout for an admin list. Views belong to a role rather than a person so
// a useful queue assembled by one operator is immediately available to the rest of that role.
public sealed class AdminListView : AuditableEntity
{
    public required string Role { get; set; }
    public required string ListKey { get; set; }
    public required string Name { get; set; }
    public string FilterJson { get; set; } = "{}";
    public string ColumnsCsv { get; set; } = string.Empty;
}

// A person's own admin setting (shell redesign R4), such as the layout density, so it follows
// them to every browser and the Mac app. One row per Username + Key.
public sealed class UserPreference : AuditableEntity
{
    public required string Username { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}

// What a person did with a notification in the bell's inbox (shell redesign R5c): marked it done,
// or snoozed it until a time. The notification itself stays in its source (Docker alerts, social
// alerts, Overwatch area alerts, chat threads); Kind + ItemId point at it. Unix milliseconds,
// since SQLite can't order DateTimeOffset.
public sealed class UserNotificationState : AuditableEntity
{
    public required string Username { get; set; }
    public required string Kind { get; set; }
    public Guid ItemId { get; set; }
    public long? DoneAtUnix { get; set; }
    public long? SnoozedAtUnix { get; set; }
    public long? SnoozedUntilUnix { get; set; }
}

// A browser or installed app that gets this person's notifications as Web Push (PWA, 07-11-26).
// Endpoint is the push service's address for that device (Apple, Google, Mozilla or Microsoft);
// P256dh and Auth are the keys its messages are encrypted to (RFC 8291).
public sealed class WebPushSubscription : AuditableEntity
{
    public required string Username { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    // What the person sees in the list, e.g. "iPhone" or "Chrome on Android".
    public string? Device { get; set; }
    public long? LastDeliveredAtUnix { get; set; }
    // Failed sends in a row; the push service saying the subscription is gone removes it at once.
    public int Failures { get; set; }
}

// The server's VAPID key pair (one row): push services check each push is signed with it. The
// private key is kept encrypted with Data Protection.
public sealed class WebPushKey : AuditableEntity
{
    public required string PublicKey { get; set; }
    public required string ProtectedPrivateKey { get; set; }
}
