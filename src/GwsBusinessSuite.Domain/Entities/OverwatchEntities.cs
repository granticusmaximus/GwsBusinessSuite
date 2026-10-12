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
    // Where the bell entry opens, when it isn't the area itself (camera-watch detections).
    public string? LinkUrl { get; set; }
    // The camera a camera-watch detection is about.
    public string? CameraId { get; set; }
}

// Camera watch (CameraWatchService): the last reading the Mac app's local vision model made of
// one watched camera, so a change to "bad" can raise one alert. Times are Unix seconds.
public sealed class OverwatchCameraWatchState : AuditableEntity
{
    public required string Username { get; set; }
    public required string CameraId { get; set; }
    public required string CameraName { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public required string LastLevel { get; set; }
    public string LastSummary { get; set; } = string.Empty;
    public long LastCheckedUnix { get; set; }
    public long? LastAlertUnix { get; set; }
}

// A saved route-watch trip: an ordered list of stops (place names or "lat, lon"), re-plotted
// with current cameras/incidents/alerts whenever it's opened.
public sealed class OverwatchTrip : AuditableEntity
{
    public required string Username { get; set; }
    public required string Name { get; set; }
    public string StopsJson { get; set; } = "[]";
}

// Overwatch time machine: one traffic incident (crash, closure or hazard) and the span it was
// seen in, from incident lookups Overwatch made (IncidentHistoryRecorder). Unix seconds so the
// "active at time T" query runs in SQLite. Kept for 48 hours.
public sealed class OverwatchIncidentSighting : AuditableEntity
{
    public required string Key { get; set; }
    public required string Category { get; set; }
    public string SeverityLevel { get; set; } = string.Empty;
    public string RoadwayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public long FirstSeenUnix { get; set; }
    public long LastSeenUnix { get; set; }
}

// Overwatch morning briefing (OverwatchBriefingService): one opt-in per user. Sent once a day at
// Hour (server local time) to the bell, and to Email too when it's set and SMTP is configured.
public sealed class OverwatchBriefingSubscription : AuditableEntity
{
    public required string Username { get; set; }
    public bool Enabled { get; set; } = true;
    public int Hour { get; set; } = 7;
    public string Email { get; set; } = string.Empty;
    // yyyy-MM-dd (server local) of the last briefing sent, so a restart never sends twice.
    public string LastSentDate { get; set; } = string.Empty;
}

// Overwatch map notes (MapNoteService): a pin or a drawn area with a title and note, saved per
// user. ShareKey is set only once the owner shares it; the link carries it, so other signed-in
// admins can open that one note read-only without seeing the rest.
public sealed class OverwatchMapNote : AuditableEntity
{
    public required string Username { get; set; }
    // "pin" or "area" - see MapNoteKinds.
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Color { get; set; } = "#ffd166";
    // [[lat, lon], ...]: one point for a pin, 3 or more for an area.
    public string PointsJson { get; set; } = "[]";
    public string? ShareKey { get; set; }
}
