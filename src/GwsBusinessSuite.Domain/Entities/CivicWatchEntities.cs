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
    public DateTimeOffset? SummarizedAt { get; set; }
    public string? LastError { get; set; }
    public int SummaryAttempts { get; set; }
}
