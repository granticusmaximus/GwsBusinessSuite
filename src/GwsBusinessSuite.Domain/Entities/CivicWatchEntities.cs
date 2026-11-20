using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// Single row: where "home" is for Civic Watch, the districts that point falls in, the keywords
// for the Federal Register and Grants.gov panels, and where bill-change alerts go. The
// Georgia/Houston County sources themselves stay fixed - only distances, districts and
// keyword searches follow this row.
public sealed class CivicWatchSettings : AuditableEntity
{
    public string HomeLabel { get; set; } = CivicWatchDefaults.HomeLabel;
    public double HomeLatitude { get; set; } = CivicWatchDefaults.HomeLatitude;
    public double HomeLongitude { get; set; } = CivicWatchDefaults.HomeLongitude;
    public string? HomeAddress { get; set; }

    // Resolved from the home point by the Census geocoder; null until a lookup succeeds.
    public string? StateCode { get; set; }
    public string? CountyName { get; set; }
    public int? CongressionalDistrict { get; set; }
    public int? StateSenateDistrict { get; set; }
    public int? StateHouseDistrict { get; set; }
    public DateTimeOffset? DistrictsResolvedAt { get; set; }

    // One phrase per line.
    public string FederalRegisterKeywords { get; set; } = string.Empty;
    public string GrantKeywords { get; set; } = string.Empty;

    public bool AlertsEnabled { get; set; }
    public string? AlertRecipient { get; set; }

    // Null for the shared default; a username for that person's own settings (20-11-26).
    public string? Username { get; set; }

    // Streets, neighbourhoods and places to look for in the county's meeting papers, one per line.
    public string MentionTerms { get; set; } = string.Empty;
}

// What Civic Watch remembers per person (20-11-26): when they last looked, for "what changed
// since your last visit".
public sealed class CivicWatchUserState : AuditableEntity
{
    public required string Username { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }
    // The visit before the current one: what "since your last visit" counts from.
    public DateTimeOffset? PreviousVisitAt { get; set; }
    // The secret in their calendar subscription link (.ics), made on first use.
    public string? CalendarToken { get; set; }
    // Agenda items followed across meetings, one phrase per line.
    public string FollowedItems { get; set; } = string.Empty;
    // When their weekly civic page in Sentinel was last built.
    public DateTimeOffset? WeeklyPageBuiltAt { get; set; }
}

// One member's vote on one roll call, kept so scorecards span more than the latest votes (20-11-26).
// Georgia's U.S. delegation and the Georgia General Assembly only.
public sealed class CivicMemberVote : AuditableEntity
{
    // "federal" or "state".
    public required string Level { get; set; }
    public required string Chamber { get; set; }
    public required string MemberId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Party { get; set; }
    // Chamber, roll call number and date: one roll call.
    public required string RollCallKey { get; set; }
    public string Measure { get; set; } = string.Empty;
    public string Vote { get; set; } = string.Empty;
    // The side most of the member's party took ("yea"/"nay"), when it could be told.
    public string? PartyMajority { get; set; }
    public DateTimeOffset? VotedAt { get; set; }
}

public static class CivicWatchDefaults
{
    public const string HomeLabel = "Kathleen, Houston County, Georgia";
    public const double HomeLatitude = 32.4610;
    public const double HomeLongitude = -83.6152;
}

// A Georgia or federal bill an admin follows. Status is re-checked hourly; a change is emailed
// (when alerts are on) and raised as the "Civic Watch Bill Status Changed" automation trigger.
public sealed class WatchedBill : AuditableEntity
{
    // "ga" or "us".
    public required string Jurisdiction { get; set; }
    // Normalized label, e.g. "HB 68" or "H.R. 1".
    public required string Label { get; set; }
    // Georgia: "HB"/"SB"/"HR"/"SR". Federal: congress.gov bill type ("hr", "s", "hjres"...).
    public required string BillType { get; set; }
    public int Number { get; set; }
    // Federal only.
    public int? Congress { get; set; }
    public string Title { get; set; } = string.Empty;
    public string LatestStatus { get; set; } = string.Empty;
    public string? LatestStatusDate { get; set; }
    public string OfficialUrl { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    public string? LastError { get; set; }
}

public static class WatchedBillJurisdictions
{
    public const string Georgia = "ga";
    public const string Federal = "us";
}

// The first time a Civic Watch item (announcement, meeting, law, vote) was seen. Drives the
// "New" badges and the "What changed" strip. Rows written by the very first refresh are a
// baseline, so a fresh install does not mark everything as new.
public sealed class CivicItemSighting : AuditableEntity
{
    public required string ItemKey { get; set; }
    public required string Desk { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; }
    public bool IsBaseline { get; set; }
}

// A county commission agenda or minutes PDF, with its extracted text summarized once.
public sealed class CivicMeetingDocument : AuditableEntity
{
    public required string Url { get; set; }
    // yyyy-MM-dd.
    public required string MeetingDate { get; set; }
    // "Agenda" or "Minutes".
    public required string Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? AiSummary { get; set; }
    // The PDF's text, kept for search and place mentions (20-11-26).
    public string? Text { get; set; }
    public DateTimeOffset? SummarizedAt { get; set; }
    public string? LastError { get; set; }
    public int SummaryAttempts { get; set; }
}
