namespace GwsBusinessSuite.Application.Privacy;

public interface IPrivacyOperationsService
{
    Task<PrivacyDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<PrivacyRequestView> CreateRequestAsync(CreatePrivacyRequest input, CancellationToken cancellationToken = default);
    Task VerifyIdentityAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task CompleteRequestAsync(Guid requestId, string status, string decisionNotes, CancellationToken cancellationToken = default);
    Task<SubjectDataExport> ExportSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<SecurityIncidentView> CreateIncidentAsync(CreateSecurityIncident input, CancellationToken cancellationToken = default);
    Task AddIncidentUpdateAsync(Guid incidentId, string updateType, string notes, CancellationToken cancellationToken = default);
    Task UpdateIncidentAssessmentAsync(Guid incidentId, string riskAssessment, bool regulatorNotificationRequired,
        DateTimeOffset? regulatorNotifiedAt, string status, CancellationToken cancellationToken = default);
    Task UpdateRetentionPolicyAsync(Guid policyId, int retentionDays, string legalBasis,
        bool enabled, bool automationApproved, CancellationToken cancellationToken = default);
}
