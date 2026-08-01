namespace GwsBusinessSuite.Application.Privacy;

public static class PrivacyRequestTypes
{
    public const string Access = "Access";
    public const string Erasure = "Erasure";
    public const string Correction = "Correction";
    public const string Restriction = "Restriction";
    public static readonly string[] All = [Access, Erasure, Correction, Restriction];
}

public static class PrivacyRequestStatuses
{
    public const string Received = "Received";
    public const string IdentityVerified = "IdentityVerified";
    public const string InReview = "InReview";
    public const string Fulfilled = "Fulfilled";
    public const string Denied = "Denied";
    public static readonly string[] All = [Received, IdentityVerified, InReview, Fulfilled, Denied];
}

public static class IncidentStatuses
{
    public const string Open = "Open";
    public const string Contained = "Contained";
    public const string Resolved = "Resolved";
    public static readonly string[] All = [Open, Contained, Resolved];
}

public sealed record PrivacyDashboard(
    IReadOnlyList<RetentionPolicyView> RetentionPolicies,
    IReadOnlyList<PrivacyRequestView> Requests,
    IReadOnlyList<SecurityIncidentView> Incidents,
    int OverdueRequestCount,
    int NotificationDeadlineCount);

public sealed record RetentionPolicyView(Guid Id, string DataCategory, string Description,
    int RetentionDays, string LegalBasis, bool IsEnabled, bool AutomationApproved,
    DateTimeOffset? LastReviewedAt, int EligibleRecordCount);

public sealed record PrivacyRequestView(Guid Id, string RequestNumber, string RequestType,
    string SubjectIdentifier, string Status, DateTimeOffset ReceivedAt, DateTimeOffset DueAt,
    DateTimeOffset? IdentityVerifiedAt, DateTimeOffset? CompletedAt, string DecisionNotes);

public sealed record SecurityIncidentView(Guid Id, string IncidentNumber, string Title,
    string Summary, string Severity, string Status, DateTimeOffset DetectedAt,
    DateTimeOffset? BreachAwarenessAt, bool PersonalDataInvolved, bool EphiInvolved,
    string RiskAssessment, bool RegulatorNotificationRequired,
    DateTimeOffset? RegulatorNotificationDueAt, DateTimeOffset? RegulatorNotifiedAt,
    DateTimeOffset? ContainedAt, DateTimeOffset? ResolvedAt, string Owner,
    IReadOnlyList<IncidentUpdateView> Updates);

public sealed record IncidentUpdateView(DateTimeOffset OccurredAt, string UpdateType, string Notes, string Author);
public sealed record CreatePrivacyRequest(string RequestType, string SubjectIdentifier, string DecisionNotes = "");
public sealed record CreateSecurityIncident(string Title, string Summary, string Severity,
    DateTimeOffset DetectedAt, bool PersonalDataInvolved, bool EphiInvolved,
    DateTimeOffset? BreachAwarenessAt, string Owner);
public sealed record SubjectDataExport(string FileName, byte[] Content);

