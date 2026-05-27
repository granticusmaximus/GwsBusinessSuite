using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DockerDeploymentService : IDockerDeploymentService
{
    public Task<string> DeployAsync(string appName, string dockerfilePath, CancellationToken ct = default) =>
        Task.FromResult($"Docker deployment integration is not configured. Requested deployment: {appName} using {dockerfilePath}.");
}
