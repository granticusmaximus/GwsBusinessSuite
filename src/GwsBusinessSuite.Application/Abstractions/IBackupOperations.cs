namespace GwsBusinessSuite.Application.Abstractions;

// Application/Infrastructure cannot reference GwsBusinessSuite.Web.Services.DatabaseBackupService
// directly - project references only flow Web -> Infrastructure -> Application -> Domain. This
// narrow seam lets PrivacyOperationsService (Infrastructure) trigger a real backup before an
// irreversible erasure deletion without relocating that already-hardened, deploy-critical
// service. The Web-side implementation is a thin delegation to the existing DatabaseBackupService.
public interface IBackupOperations
{
    Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);
}
