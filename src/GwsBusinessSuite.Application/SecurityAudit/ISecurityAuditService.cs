namespace GwsBusinessSuite.Application.SecurityAudit;

public interface ISecurityAuditService
{
    Task<Guid> RecordAsync(SecurityAuditInput input, CancellationToken cancellationToken = default);
    Task<SecurityAuditPage> QueryAsync(SecurityAuditQuery query, CancellationToken cancellationToken = default);
    Task<SecurityAuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}
