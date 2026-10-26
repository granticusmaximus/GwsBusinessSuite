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
