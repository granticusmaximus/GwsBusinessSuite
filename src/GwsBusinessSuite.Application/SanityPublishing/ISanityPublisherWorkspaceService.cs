namespace GwsBusinessSuite.Application.SanityPublishing;

public interface ISanityPublisherWorkspaceService
{
    Task<SanityPublisherWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
