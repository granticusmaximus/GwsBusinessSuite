namespace GwsBusinessSuite.Application.DockerHealth;

public interface IDockerHealthService
{
    Task<ContainerListResult> ListContainersAsync(CancellationToken cancellationToken = default);

    Task<ContainerDetailView?> GetContainerDetailsAsync(string containerName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DockerHealthAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default);

    Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default);

    Task MarkAllAlertsReadAsync(CancellationToken cancellationToken = default);

    Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default);

    Task<DockerActionResult> StartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default);

    Task<DockerActionResult> StopContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default);

    Task<DockerActionResult> RestartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default);

    // Container must already be stopped - this deliberately does not stop it first, so
    // removal is never a surprise side effect of a single click.
    Task<DockerActionResult> RemoveContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default);

    // Pulls the latest image for the container's current image tag. Does not touch the
    // running container - see RecreateContainerAsync for applying the pulled image.
    Task<DockerActionResult> PullImageAsync(string containerName, string performedBy, CancellationToken cancellationToken = default);

    // Stops, removes, and recreates the container from its own inspected config against
    // whatever image is now cached locally (see PullImageAsync).
    Task<DockerActionResult> RecreateContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default);

    // One-shot command runner (not an interactive PTY) - runs `command` via /bin/sh -c
    // and returns its combined stdout/stderr.
    Task<DockerActionResult> ExecCommandAsync(string containerName, string command, string performedBy, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DockerActionLogView>> ListActionLogsAsync(string? containerName, CancellationToken cancellationToken = default);
}
