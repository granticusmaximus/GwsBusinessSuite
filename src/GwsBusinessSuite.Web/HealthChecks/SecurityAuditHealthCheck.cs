using GwsBusinessSuite.Application.SecurityAudit;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GwsBusinessSuite.Web.HealthChecks;

public sealed class SecurityAuditHealthCheck(ISecurityAuditService securityAudit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await securityAudit.VerifyIntegrityAsync(cancellationToken);
            return result.IsValid
                ? HealthCheckResult.Healthy($"Security audit chain verified across {result.EventsChecked} event(s).")
                : HealthCheckResult.Unhealthy("Security audit integrity verification failed.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Security audit integrity could not be verified.", ex);
        }
    }
}
