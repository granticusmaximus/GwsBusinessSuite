using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// Append-only security evidence. Application code must use ISecurityAuditService rather
// than updating rows directly so the hash chain remains intact. Network context is
// encrypted and never returned by the normal audit-log query.
public sealed class SecurityAuditEvent : AuditableEntity
{
    public long ChainSequence { get; set; }
    public long OccurredAtUnixSeconds { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string ActorUsername { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public string? NetworkAddressProtected { get; set; }
    public string PreviousEventHash { get; set; } = string.Empty;
    public string EventHash { get; set; } = string.Empty;
}

public sealed class PrivacyRetentionPolicy : AuditableEntity
{
    public required string DataCategory { get; set; }
    public string Description { get; set; } = string.Empty;
    public int RetentionDays { get; set; }
    public string LegalBasis { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool AutomationApproved { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
}

public sealed class PrivacyRequest : AuditableEntity
{
    public required string RequestNumber { get; set; }
    public required string RequestType { get; set; }
    public required string SubjectIdentifier { get; set; }
    public string Status { get; set; } = "Received";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? IdentityVerifiedAt { get; set; }
    public string? IdentityVerifiedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string DecisionNotes { get; set; } = string.Empty;
}

public sealed class SecurityIncident : AuditableEntity
{
    public required string IncidentNumber { get; set; }
    public required string Title { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Severity { get; set; } = "Medium";
    public string Status { get; set; } = "Open";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? BreachAwarenessAt { get; set; }
    public bool PersonalDataInvolved { get; set; }
    public bool EphiInvolved { get; set; }
    public string RiskAssessment { get; set; } = "Pending";
    public bool RegulatorNotificationRequired { get; set; }
    public DateTimeOffset? RegulatorNotificationDueAt { get; set; }
    public DateTimeOffset? RegulatorNotifiedAt { get; set; }
    public DateTimeOffset? ContainedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string Owner { get; set; } = string.Empty;
}

public sealed class SecurityIncidentUpdate : AuditableEntity
{
    public Guid SecurityIncidentId { get; set; }
    public required string UpdateType { get; set; }
    public required string Notes { get; set; }
    public SecurityIncident? Incident { get; set; }
}
