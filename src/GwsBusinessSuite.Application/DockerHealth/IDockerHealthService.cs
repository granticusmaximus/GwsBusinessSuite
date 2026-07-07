namespace GwsBusinessSuite.Application.DockerHealth;

public interface IDockerHealthService
{
    Task<ContainerListResult> ListContainersAsync(CancellationToken cancellationToken = default);

    Task<ContainerDetailView?> GetContainerDetailsAsync(string containerName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DockerHealthAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default);

    Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default);

    Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default);
}
