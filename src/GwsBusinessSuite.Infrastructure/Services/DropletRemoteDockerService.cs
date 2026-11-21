using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DropletRemoteDockerService(
    IAppDbContext dbContext,
    ISecretProtector secretProtector,
    ILogger<DropletRemoteDockerService> logger) : IDropletRemoteDockerService
{
    private const string SectionImages = "===IMAGES===";
    private const string SectionDisk = "===DF===";
    private const string SectionOllama = "===OLLAMA===";

    public async Task<DropletRemoteInspectionResult> InspectDropletAsync(ActiveDropletView droplet, CancellationToken cancellationToken = default)
    {
        var command =
            $"echo '{SectionImages}' && docker image ls --format '{{{{.Repository}}}}\\t{{{{.Tag}}}}\\t{{{{.ID}}}}\\t{{{{.Size}}}}\\t{{{{.CreatedAt}}}}' 2>/dev/null || true; " +
            $"echo '{SectionDisk}' && docker system df --format '{{{{.Type}}}}\\t{{{{.TotalCount}}}}\\t{{{{.Active}}}}\\t{{{{.Size}}}}\\t{{{{.Reclaimable}}}}' 2>/dev/null || true; " +
            $"echo '{SectionOllama}' && if command -v ollama >/dev/null 2>&1; then echo 'INSTALLED=1'; " +
            "echo \"OLLAMA_SVC=$(systemctl is-active ollama 2>/dev/null || echo inactive)\"; " +
            "echo \"WATCHDOG_TMR=$(systemctl is-active gws-ollama-watchdog.timer 2>/dev/null || echo inactive)\"; " +
            "ollama list 2>/dev/null || true; else echo 'INSTALLED=0'; fi";

        var exec = await ExecuteSshCommandAsync(droplet, command, TimeSpan.FromSeconds(15), cancellationToken);
        if (!exec.Succeeded)
        {
            return new DropletRemoteInspectionResult(false, [], [], null, exec.ErrorMessage);
        }

        var (imagesBlock, diskBlock, ollamaBlock) = SplitInspectionSections(exec.Stdout);
        var images = ParseDockerImagesOutput(imagesBlock);
        var diskUsage = ParseDockerSystemDfOutput(diskBlock);
        var aiRuntime = ParseAiRuntimeOutput(ollamaBlock);

        return new DropletRemoteInspectionResult(true, images, diskUsage, aiRuntime);
    }

    public async Task<DropletRemoteLogsResult> TailContainerLogsAsync(
        ActiveDropletView droplet, string containerName, int tailLines = 100, CancellationToken cancellationToken = default)
    {
        if (!DropletDockerIdentifier.IsValid(containerName))
        {
            return new DropletRemoteLogsResult(false, string.Empty, "Invalid container name.");
        }

        var clampedLines = Math.Clamp(tailLines, 10, 500);
        var exec = await ExecuteSshCommandAsync(
            droplet,
            $"docker logs --tail {clampedLines} --timestamps {containerName} 2>&1",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        return exec.Succeeded
            ? new DropletRemoteLogsResult(true, string.IsNullOrWhiteSpace(exec.Stdout) ? "(no log output)" : exec.Stdout)
            : new DropletRemoteLogsResult(false, string.Empty, exec.ErrorMessage ?? exec.Stderr);
    }

    public Task<DockerActionResult> PullImageAsync(
        ActiveDropletView droplet, string image, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!DropletDockerIdentifier.IsValid(image))
        {
            return Task.FromResult(new DockerActionResult(false, "Invalid image reference."));
        }

        return RunAuditedSshActionAsync(
            droplet,
            target: image,
            action: "RemotePull",
            command: $"docker image pull {image}",
            performedBy: performedBy,
            timeout: TimeSpan.FromMinutes(3),
            successSummary: $"Pulled {image} on {droplet.Name}.",
            cancellationToken: cancellationToken);
    }

    public Task<DockerActionResult> RestartContainerAsync(
        ActiveDropletView droplet, string containerName, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!DropletDockerIdentifier.IsValid(containerName))
        {
            return Task.FromResult(new DockerActionResult(false, "Invalid container name."));
        }

        return RunAuditedSshActionAsync(
            droplet,
            target: containerName,
            action: "RemoteRestart",
            command: $"docker restart {containerName}",
            performedBy: performedBy,
            timeout: TimeSpan.FromSeconds(45),
            successSummary: $"Restarted container {containerName} on {droplet.Name}.",
            cancellationToken: cancellationToken);
    }

    public Task<DockerActionResult> PruneDanglingImagesAsync(
        ActiveDropletView droplet, string performedBy, CancellationToken cancellationToken = default) =>
        RunAuditedSshActionAsync(
            droplet,
            target: "images",
            action: "PruneDangling",
            command: "docker image prune -f",
            performedBy: performedBy,
            timeout: TimeSpan.FromSeconds(45),
            successSummary: $"Pruned dangling images on {droplet.Name}.",
            cancellationToken: cancellationToken);

    public Task<DockerActionResult> PruneStaleImagesAsync(
        ActiveDropletView droplet, string performedBy, CancellationToken cancellationToken = default) =>
        RunAuditedSshActionAsync(
            droplet,
            target: "images",
            action: "PruneStale",
            command: "docker image prune -a -f --filter \"until=168h\"",
            performedBy: performedBy,
            timeout: TimeSpan.FromMinutes(2),
            successSummary: $"Pruned unused images older than 7 days on {droplet.Name}.",
            cancellationToken: cancellationToken);

    public Task<DockerActionResult> RestartOllamaAsync(
        ActiveDropletView droplet, string performedBy, CancellationToken cancellationToken = default) =>
        RunAuditedSshActionAsync(
            droplet,
            target: "ollama",
            action: "RestartOllama",
            command: "systemctl restart ollama",
            performedBy: performedBy,
            timeout: TimeSpan.FromSeconds(30),
            successSummary: $"Restarted ollama service on {droplet.Name}.",
            cancellationToken: cancellationToken);

    public static IReadOnlyList<DropletCachedImageView> ParseDockerImagesOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var images = new List<DropletCachedImageView>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split('\t');
            if (parts.Length < 4)
            {
                continue;
            }

            var repo = parts[0].Trim();
            var tag = parts[1].Trim();
            var id = parts[2].Trim();
            var size = parts[3].Trim();
            var created = parts.Length >= 5 ? parts[4].Trim() : string.Empty;
            if (repo.Length == 0 || id.Length == 0)
            {
                continue;
            }

            images.Add(new DropletCachedImageView(repo, tag, id, size, created));
        }

        return images
            .OrderBy(i => i.IsDangling)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<DropletDockerDiskUsageView> ParseDockerSystemDfOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var rows = new List<DropletDockerDiskUsageView>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split('\t');
            if (parts.Length < 5)
            {
                continue;
            }

            rows.Add(new DropletDockerDiskUsageView(
                parts[0].Trim(),
                parts[1].Trim(),
                parts[2].Trim(),
                parts[3].Trim(),
                parts[4].Trim()));
        }

        return rows;
    }

    public static DropletAiRuntimeView ParseAiRuntimeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new DropletAiRuntimeView(false, "unknown", "unknown", []);
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var installed = false;
        var ollamaState = "unknown";
        var watchdogState = "unknown";
        var models = new List<DropletOllamaModelView>();
        var inTable = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "INSTALLED=1")
            {
                installed = true;
                continue;
            }
            if (trimmed == "INSTALLED=0")
            {
                return new DropletAiRuntimeView(false, "not-installed", "not-installed", []);
            }
            if (trimmed.StartsWith("OLLAMA_SVC=", StringComparison.Ordinal))
            {
                ollamaState = trimmed["OLLAMA_SVC=".Length..].Trim();
                continue;
            }
            if (trimmed.StartsWith("WATCHDOG_TMR=", StringComparison.Ordinal))
            {
                watchdogState = trimmed["WATCHDOG_TMR=".Length..].Trim();
                continue;
            }
            if (trimmed.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("SIZE", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                continue;
            }
            if (!inTable)
            {
                continue;
            }

            // `ollama list` columns are separated by 2+ spaces: NAME   ID   SIZE   MODIFIED
            var columns = System.Text.RegularExpressions.Regex.Split(trimmed, @"\s{2,}");
            if (columns.Length >= 3)
            {
                models.Add(new DropletOllamaModelView(
                    columns[0].Trim(),
                    columns[1].Trim(),
                    columns[2].Trim(),
                    columns.Length >= 4 ? columns[3].Trim() : string.Empty));
            }
        }

        return new DropletAiRuntimeView(installed, ollamaState, watchdogState, models);
    }

    private static (string Images, string Disk, string Ollama) SplitInspectionSections(string stdout)
    {
        var imagesIdx = stdout.IndexOf(SectionImages, StringComparison.Ordinal);
        var diskIdx = stdout.IndexOf(SectionDisk, StringComparison.Ordinal);
        var ollamaIdx = stdout.IndexOf(SectionOllama, StringComparison.Ordinal);

        if (imagesIdx < 0 || diskIdx < 0 || ollamaIdx < 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var imagesStart = imagesIdx + SectionImages.Length;
        var diskStart = diskIdx + SectionDisk.Length;
        var ollamaStart = ollamaIdx + SectionOllama.Length;

        var images = diskIdx > imagesStart ? stdout[imagesStart..diskIdx] : string.Empty;
        var disk = ollamaIdx > diskStart ? stdout[diskStart..ollamaIdx] : string.Empty;
        var ollama = ollamaStart < stdout.Length ? stdout[ollamaStart..] : string.Empty;
        return (images, disk, ollama);
    }

    private async Task<DockerActionResult> RunAuditedSshActionAsync(
        ActiveDropletView droplet,
        string target,
        string action,
        string command,
        string performedBy,
        TimeSpan timeout,
        string successSummary,
        CancellationToken cancellationToken)
    {
        var exec = await ExecuteSshCommandAsync(droplet, command, timeout, cancellationToken);
        var summary = exec.Succeeded
            ? (string.IsNullOrWhiteSpace(exec.Stdout) ? successSummary : $"{successSummary} {Truncate(exec.Stdout.Trim(), 300)}")
            : (exec.ErrorMessage ?? "SSH command failed.");

        await LogActionAsync($"{droplet.Name}:{target}", action, command, exec.Succeeded, Truncate(summary, 2000), performedBy, cancellationToken);
        return new DockerActionResult(exec.Succeeded, summary);
    }

    private async Task LogActionAsync(
        string containerName,
        string action,
        string command,
        bool succeeded,
        string resultSummary,
        string performedBy,
        CancellationToken cancellationToken)
    {
        dbContext.DockerActionLogs.Add(new DockerActionLog
        {
            ContainerName = containerName,
            Action = action,
            Command = command,
            Succeeded = succeeded,
            ResultSummary = resultSummary,
            PerformedBy = performedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = performedBy
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record SshExecResult(bool Succeeded, string Stdout, string Stderr, string? ErrorMessage = null);

    private async Task<SshExecResult> ExecuteSshCommandAsync(
        ActiveDropletView droplet,
        string command,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.SshPrivateKey))
        {
            return new SshExecResult(false, string.Empty, string.Empty, "No SSH private key saved yet. Add one in Terminal settings.");
        }

        string privateKeyPem;
        string? passphrase;
        try
        {
            privateKeyPem = secretProtector.Unprotect(row.SshPrivateKey);
            passphrase = string.IsNullOrEmpty(row.SshPrivateKeyPassphrase)
                ? null
                : secretProtector.Unprotect(row.SshPrivateKeyPassphrase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to decrypt stored SSH private key.");
            return new SshExecResult(false, string.Empty, string.Empty, "Saved SSH private key could not be decrypted.");
        }

        var hosts = new[] { droplet.PublicIpAddress, droplet.PrivateIpAddress }
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        if (hosts.Count == 0)
        {
            return new SshExecResult(false, string.Empty, string.Empty, $"Droplet {droplet.Name} has no IP address.");
        }

        var username = string.IsNullOrWhiteSpace(row.SshUsername) ? "root" : row.SshUsername;
        var port = row.SshPort <= 0 ? 22 : row.SshPort;
        var expectedFingerprint = droplet.IsCurrentHost ? row.SshHostKeyFingerprint : null;

        string? lastError = null;
        foreach (var host in hosts)
        {
            try
            {
                return await Task.Run(() =>
                {
                    using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem));
                    var keyFile = passphrase is null
                        ? new PrivateKeyFile(keyStream)
                        : new PrivateKeyFile(keyStream, passphrase);
                    var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
                    var connectionInfo = new ConnectionInfo(host, port, username, authMethod)
                    {
                        Timeout = TimeSpan.FromSeconds(6)
                    };
                    using var client = new SshClient(connectionInfo);
                    client.HostKeyReceived += (_, e) =>
                    {
                        if (string.IsNullOrEmpty(expectedFingerprint))
                        {
                            e.CanTrust = true;
                            return;
                        }

                        e.CanTrust = SshTerminalService.IsHostKeyTrusted(expectedFingerprint, e.FingerPrintSHA256);
                    };

                    client.Connect();
                    try
                    {
                        using var cmd = client.CreateCommand(command);
                        cmd.CommandTimeout = commandTimeout;
                        var stdout = cmd.Execute();
                        var stderr = cmd.Error ?? string.Empty;
                        if (cmd.ExitStatus != 0)
                        {
                            var msg = string.IsNullOrWhiteSpace(stderr)
                                ? $"Command exited with code {cmd.ExitStatus}."
                                : stderr.Trim();
                            return new SshExecResult(false, stdout, stderr, msg);
                        }

                        return new SshExecResult(true, stdout, stderr);
                    }
                    finally
                    {
                        if (client.IsConnected) client.Disconnect();
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is SshConnectionException or SshException or System.Net.Sockets.SocketException or TimeoutException)
            {
                lastError = $"SSH to {host} failed: {ex.Message}";
            }
        }

        return new SshExecResult(false, string.Empty, string.Empty, lastError ?? "Could not reach droplet over SSH.");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
