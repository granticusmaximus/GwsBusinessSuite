using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// An area an admin asked Overwatch to watch (saved from the globe's current view). Checked every
// 10 minutes; anything new that clears the area's filters becomes an OverwatchAreaAlert.
public sealed class OverwatchWatchArea : AuditableEntity
{
    public required string Username { get; set; }
    public required string Name { get; set; }
    public double North { get; set; }
    public double South { get; set; }
    public double East { get; set; }
    public double West { get; set; }
    public bool WatchIncidents { get; set; } = true;
    public bool WatchWeather { get; set; } = true;
    public bool WatchHazards { get; set; } = true;
    // "major", "moderate" or "any" - see OverwatchAreaSeverity.
    public string MinSeverity { get; set; } = "moderate";
    // Keys of items already reported (or present at the baseline check), newest last, capped.
    public string SeenKeysJson { get; set; } = "[]";
    public DateTimeOffset? LastCheckedAt { get; set; }
}

public static class OverwatchAreaSeverity
{
    public const string Major = "major";
    public const string Moderate = "moderate";
    public const string Any = "any";
}

// One bell notification: "new things in <area>" from a single check.
public sealed class OverwatchAreaAlert : AuditableEntity
{
    public required string Username { get; set; }
    public Guid AreaId { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
