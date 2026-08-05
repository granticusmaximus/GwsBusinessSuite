using System.Diagnostics;
using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DockerDeploymentService(
    ILogger<DockerDeploymentService> logger, string dockerExecutable = "docker") : IDockerDeploymentService
{
    // Builds the image locally via the Docker CLI rather than a remote Docker Engine API
    // client — this matches how the project already deploys (docker-compose over SSH on
    // the droplet, see .github/workflows/deploy.yml), and avoids needing a registry,
    // credentials, or a long-lived daemon connection just to build an image.
    public async Task<string> DeployAsync(string appName, string dockerfilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new ArgumentException("App name is required.", nameof(appName));
        }

        if (string.IsNullOrWhiteSpace(dockerfilePath) || !File.Exists(dockerfilePath))
        {
            return $"Docker deployment failed: Dockerfile not found at '{dockerfilePath}'.";
        }

        var fullDockerfilePath = Path.GetFullPath(dockerfilePath);
        var contextDirectory = Path.GetDirectoryName(fullDockerfilePath) ?? Directory.GetCurrentDirectory();
        var imageTag = appName.Trim().ToLowerInvariant();

        int exitCode;
        string output;
        try
        {
            (exitCode, output) = await RunDockerCommandAsync(
                ["build", "-t", imageTag, "-f", fullDockerfilePath, contextDirectory],
                ct);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Unable to run the Docker CLI while deploying '{ImageTag}'.", imageTag);
            return $"Docker deployment failed: could not run the Docker CLI ({ex.Message}).";
        }

        return exitCode == 0
            ? $"Docker image '{imageTag}' built successfully.\n{output}"
            : $"Docker build failed for '{imageTag}' (exit code {exitCode}).\n{output}";
    }

    // Takes each argument as its own array entry (ArgumentList) rather than a single
    // pre-formatted command-line string - since UseShellExecute is false there's no shell
    // to inject into either way, but ArgumentList also means an appName containing a quote
    // or space can't be misread as extra Docker CLI flags, and callers no longer have to
    // remember to manually quote paths themselves.
    private async Task<(int ExitCode, string Output)> RunDockerCommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        var combined = string.Join('\n', new[] { stdOut, stdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return (process.ExitCode, combined);
    }
}
