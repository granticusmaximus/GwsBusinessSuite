using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class DockerDeploymentServiceTests
{
    [Fact]
    public async Task DeployAsync_ShouldReturnFailureMessage_WhenDockerfileDoesNotExist()
    {
        var service = new DockerDeploymentService();

        var result = await service.DeployAsync("my-app", "/nonexistent/Dockerfile");

        result.Should().Contain("Dockerfile not found");
        result.Should().Contain("/nonexistent/Dockerfile");
    }

    [Fact]
    public async Task DeployAsync_ShouldThrow_WhenAppNameIsBlank()
    {
        var service = new DockerDeploymentService();

        var action = async () => await service.DeployAsync(string.Empty, "/nonexistent/Dockerfile");

        await action.Should().ThrowAsync<ArgumentException>();
    }
}
