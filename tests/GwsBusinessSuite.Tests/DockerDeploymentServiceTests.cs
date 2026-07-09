using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DockerDeploymentServiceTests
{
    [Fact]
    public async Task DeployAsync_ShouldReturnFailureMessage_WhenDockerfileDoesNotExist()
    {
        var service = new DockerDeploymentService(NullLogger<DockerDeploymentService>.Instance);

        var result = await service.DeployAsync("my-app", "/nonexistent/Dockerfile");

        result.Should().Contain("Dockerfile not found");
        result.Should().Contain("/nonexistent/Dockerfile");
    }

    [Fact]
    public async Task DeployAsync_ShouldThrow_WhenAppNameIsBlank()
    {
        var service = new DockerDeploymentService(NullLogger<DockerDeploymentService>.Instance);

        var action = async () => await service.DeployAsync(string.Empty, "/nonexistent/Dockerfile");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeployAsync_ShouldReturnFailureMessage_WhenDockerCliCannotBeStarted()
    {
        // Points at a binary name that can't possibly exist on PATH, deterministically
        // exercising the "docker isn't available" path without depending on whether this
        // environment actually has Docker installed.
        var service = new DockerDeploymentService(
            NullLogger<DockerDeploymentService>.Instance, dockerExecutable: "gws-nonexistent-docker-binary");

        var dockerfilePath = Path.GetTempFileName();
        try
        {
            var result = await service.DeployAsync("my-app", dockerfilePath);

            result.Should().Contain("Docker deployment failed");
            result.Should().Contain("could not run the Docker CLI");
        }
        finally
        {
            File.Delete(dockerfilePath);
        }
    }
}
