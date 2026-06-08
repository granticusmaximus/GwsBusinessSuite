namespace GwsBusinessSuite.Application.Deployments;

public interface IDeploymentWorkspaceService
{
    Task<DeploymentWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
