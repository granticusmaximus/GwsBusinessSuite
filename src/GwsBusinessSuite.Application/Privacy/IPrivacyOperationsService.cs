namespace GwsBusinessSuite.Application.Privacy;

public interface IPrivacyOperationsService
{
    Task<PrivacyDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<PrivacyRequestView> CreateRequestAsync(CreatePrivacyRequest input, CancellationToken cancellationToken = default);
    Task VerifyIdentityAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task CompleteRequestAsync(Guid requestId, string status, string decisionNotes,
        bool erasureDataDeletionConfirmed = false, CancellationToken cancellationToken = default);
    Task<SubjectDataExport> ExportSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default);
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
