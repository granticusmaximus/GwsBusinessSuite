namespace GwsBusinessSuite.Application.SecurityAudit;

public static class SecurityAuditCategories
{
    public const string Authentication = "Authentication";
    public const string AccountAdministration = "AccountAdministration";
    public const string Authorization = "Authorization";
    public const string DataAccess = "DataAccess";
    public const string DataLifecycle = "DataLifecycle";
    public const string Integration = "Integration";
    public const string AiEgress = "AiEgress";
    public const string Infrastructure = "Infrastructure";
    public const string SecurityOperations = "SecurityOperations";
}

public static class SecurityAuditOutcomes
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Denied = "Denied";
}

public static class SecurityAuditSeverities
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string High = "High";
    public const string Critical = "Critical";
}

public sealed record SecurityAuditInput(
    string Category,
    string Action,
    string Outcome,
    string Severity = SecurityAuditSeverities.Information,
    string? TargetType = null,
    string? TargetId = null,
    IReadOnlyDictionary<string, string?>? Details = null,
    string? NetworkAddress = null,
    string? ActorUsername = null,
    string? CorrelationId = null);

public sealed record SecurityAuditEventView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    string Outcome,
    string Severity,
    string ActorUsername,
    string? TargetType,
    string? TargetId,
    string CorrelationId,
    IReadOnlyDictionary<string, string?> Details,
    bool HasProtectedNetworkContext);

public sealed record SecurityAuditQuery(
    string? Category = null,
    string? Outcome = null,
    string? Actor = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

public sealed record SecurityAuditPage(
    IReadOnlyList<SecurityAuditEventView> Events,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record SecurityAuditIntegrityResult(
    bool IsValid,
    int EventsChecked,
    Guid? FirstInvalidEventId = null,
    string? FailureReason = null);
