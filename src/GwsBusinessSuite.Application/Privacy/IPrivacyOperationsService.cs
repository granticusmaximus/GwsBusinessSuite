namespace GwsBusinessSuite.Application.Privacy;

public interface IPrivacyOperationsService
{
    Task<PrivacyDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<PrivacyRequestView> CreateRequestAsync(CreatePrivacyRequest input, CancellationToken cancellationToken = default);
    Task VerifyIdentityAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// For Erasure requests, Fulfilled now requires DeletionExecutedAt to already be set (i.e.
    /// DeleteSubjectDataAsync has actually run and succeeded for this exact request) - there is
    /// no confirmation-flag override. This is deliberate: a boolean attestation is unverifiable,
    /// real deletion evidence is not.
    /// </summary>
    Task CompleteRequestAsync(Guid requestId, string status, string decisionNotes,
        CancellationToken cancellationToken = default);
    Task<SubjectDataExport> ExportSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only, live-data preview of what DeleteSubjectDataAsync would delete for this Erasure
    /// request - table names, row counts, and a few sample identifiers, plus Invoice/InvoiceLineItem
    /// counts surfaced as excluded findings (never deleted automatically - needs manual review).
    /// Never mutates anything; safe to call repeatedly.
    /// </summary>
    Task<SubjectDeletionPreview> PreviewErasureAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the request's subject, takes a fresh backup first (aborting entirely if that
    /// fails), then deletes the in-scope tables inside a single transaction so a mid-run failure
    /// leaves nothing partially erased. On success, stamps DeletionExecutedAt/DeletionSummaryJson
    /// on the request, which is what CompleteRequestAsync then checks for Fulfilled. Throws if
    /// the subject cannot be resolved at all, or if the resolved AppUser is the last active admin.
    /// </summary>
    Task<SubjectDeletionSummary> DeleteSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<SecurityIncidentView> CreateIncidentAsync(CreateSecurityIncident input, CancellationToken cancellationToken = default);
    Task AddIncidentUpdateAsync(Guid incidentId, string updateType, string notes, CancellationToken cancellationToken = default);
    Task UpdateIncidentAssessmentAsync(Guid incidentId, string riskAssessment, bool regulatorNotificationRequired,
        DateTimeOffset? regulatorNotifiedAt, string status, CancellationToken cancellationToken = default);
    Task UpdateRetentionPolicyAsync(Guid policyId, int retentionDays, string legalBasis,
        bool enabled, bool automationApproved, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes records past each enabled, automation-approved retention policy's cutoff.
    /// Only policies with both IsEnabled and AutomationApproved set are acted on - both default
    /// to admin-controlled opt-in (AutomationApproved starts false for every seeded policy, and
    /// UpdateRetentionPolicyAsync refuses to ever set it for "Security audit"), so calling this
    /// is a no-op until an admin explicitly approves a category for automated deletion in the
    /// Privacy Operations UI. Returns the total number of rows deleted, for logging/testing.
    /// </summary>
    Task<int> PurgeEligibleRecordsAsync(CancellationToken cancellationToken = default);
}
