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
