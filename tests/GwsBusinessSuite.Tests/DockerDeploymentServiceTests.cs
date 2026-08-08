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
    public async Task DeployAsync_ShouldPassTheImageTagAsASingleArgument_EvenWhenItContainsSpacesAndQuotes()
    {
        // Regression guard for the actual security property RunDockerCommandAsync's own doc
        // comment claims: using ArgumentList (one array entry per argument) instead of a
        // formatted command-line string means an appName containing a space or quote can't be
        // misread as extra Docker CLI flags. Points dockerExecutable at a tiny script that
        // echoes back exactly the arguments it received, one per line, so the argument
        // boundary is directly observable instead of just trusting ArgumentList "should" work.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"gws-argecho-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nfor arg in \"$@\"; do printf '%s\\n' \"$arg\"; done\n");
        // Docker deployment (and this test's shell script) is POSIX-only - matches the rest of
        // this service, which already assumes PATH resolution semantics no Windows box has.
#pragma warning disable CA1416
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var dockerfilePath = Path.GetTempFileName();
        try
        {
            var service = new DockerDeploymentService(NullLogger<DockerDeploymentService>.Instance, dockerExecutable: scriptPath);
            var maliciousAppName = "my app\" --privileged";

            var result = await service.DeployAsync(maliciousAppName, dockerfilePath);

            var lines = result.Split('\n');
            lines.Should().Contain("build");
            lines.Should().Contain("-t");
            lines.Should().Contain(maliciousAppName, "the whole app name must arrive as one argument");
            lines.Should().NotContain("--privileged", "it must never be split out into its own argument");
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(dockerfilePath);
        }
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
