using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Web.Services;

// Thin delegation so Infrastructure-layer code (PrivacyOperationsService's erasure deletion) can
// trigger a real backup without a direct reference to DatabaseBackupService, which lives here in
// Web and can't be referenced from Infrastructure (see IBackupOperations for why).
public sealed class BackupOperations(DatabaseBackupService backups) : IBackupOperations
{
    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        backups.CreateBackupAsync(cancellationToken);
}
