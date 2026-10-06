namespace GwsBusinessSuite.Application.ThreatIntel;

// Per-page state for Threat Intelligence: saved investigations (shared by every admin) and each
// admin's "new since your last visit" marker for the KEV list.
public interface IThreatIntelWorkspaceService
{
    Task<ThreatInvestigationView> SaveInvestigationAsync(string query, IndicatorKind kind, string summary, ThreatInvestigationSnapshot snapshot,
        string username, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreatInvestigationView>> ListInvestigationsAsync(int take = 25, CancellationToken cancellationToken = default);

    Task<ThreatInvestigationSnapshot?> GetInvestigationSnapshotAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeleteInvestigationAsync(Guid id, CancellationToken cancellationToken = default);

    // Creates (or re-creates, if the earlier page was deleted) a Sentinel page with the
    // investigation's findings; returns its id.
    Task<Guid> ExportInvestigationToSentinelAsync(Guid id, string username, CancellationToken cancellationToken = default);

    // Returns when this admin last opened the KEV list (null on a first visit) and records now.
    Task<DateTimeOffset?> TouchKevVisitAsync(string username, CancellationToken cancellationToken = default);
}

public sealed record ThreatInvestigationView(
    Guid Id, string Query, string Kind, string Summary, string CreatedBy, DateTimeOffset CreatedAt, Guid? ExportedWikiPageId);

// Everything a lookup produced, stored as JSON so it can be reopened exactly as it was.
public sealed record ThreatInvestigationSnapshot(
    DomainIntelResult? Domain,
    ExposureIntelResult? Exposure,
    CveDetail? Cve,
    IReadOnlyList<IndicatorMatch> Matches);
