using Docker.DotNet;
using Docker.DotNet.Models;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DockerHealthService(IAppDbContext dbContext) : IDockerHealthService
{
    // Only reachable when the host's Docker socket is mounted read-only into this
    // container (docker-compose.yml) - not the case for local `dotnet watch`, where
    // every method below gracefully reports "unavailable" instead of throwing.
    private const string DockerSocketUri = "unix:///var/run/docker.sock";
    private const int LogTailLines = 200;

    public async Task<ContainerListResult> ListContainersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            var containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }, cancellationToken);

            var views = new List<ContainerHealthView>();
            foreach (var summary in containers)
            {
                var name = summary.Names.FirstOrDefault()?.TrimStart('/') ?? summary.ID;
                var inspect = await client.Containers.InspectContainerAsync(summary.ID, cancellationToken);
                views.Add(MapToView(name, inspect));
            }

            return new ContainerListResult(true, views.OrderBy(v => v.Name).ToList());
        }
        catch (Exception ex) when (IsSocketUnavailable(ex))
        {
            return new ContainerListResult(
                false,
                Array.Empty<ContainerHealthView>(),
                "Docker socket not available. This only works when deployed with /var/run/docker.sock mounted.");
        }
    }

    public async Task<ContainerDetailView?> GetContainerDetailsAsync(string containerName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
            var view = MapToView(containerName, inspect);

            var logParams = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Tail = LogTailLines.ToString(),
                Timestamps = true
            };
            using var logStream = await client.Containers.GetContainerLogsAsync(containerName, false, logParams, cancellationToken);
            var (stdout, stderr) = await logStream.ReadOutputToEndAsync(cancellationToken);
            var logTail = string.Join("\n", new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            var recentAlerts = await dbContext.DockerHealthAlerts
                .AsNoTracking()
                .Where(a => a.ContainerName == containerName)
                .ToListAsync(cancellationToken);

            return new ContainerDetailView
            {
                Container = view,
                LogTail = string.IsNullOrWhiteSpace(logTail) ? "(no log output)" : logTail,
                SuggestedTip = view.IsError ? SuggestTip(view) : null,
                // SQLite can't translate ORDER BY on a DateTimeOffset column, so order
                // client-side after materializing (same pattern used elsewhere in this app).
                RecentAlerts = recentAlerts
                    .OrderByDescending(a => a.CreatedAt)
                    .Take(20)
                    .Select(ToAlertView)
                    .ToList()
            };
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (IsSocketUnavailable(ex))
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DockerHealthAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default)
    {
        var query = dbContext.DockerHealthAlerts.AsNoTracking().AsQueryable();
        if (unreadOnly)
        {
            query = query.Where(a => !a.IsRead);
        }

        var alerts = await query.ToListAsync(cancellationToken);
        return alerts
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .Select(ToAlertView)
            .ToList();
    }

    public async Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default)
    {
        var alert = await dbContext.DockerHealthAlerts.FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return;
        }

        alert.IsRead = true;
        alert.UpdatedAt = DateTimeOffset.UtcNow;
        alert.UpdatedBy = "system";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.DockerHealthAlerts.CountAsync(a => !a.IsRead, cancellationToken);
    }

    public async Task<DockerActionResult> StartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Start", null, performedBy, async client =>
        {
            await client.Containers.StartContainerAsync(containerName, new ContainerStartParameters(), cancellationToken);
            return "Container started.";
        }, cancellationToken);

    public async Task<DockerActionResult> StopContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Stop", null, performedBy, async client =>
        {
            await client.Containers.StopContainerAsync(containerName, new ContainerStopParameters(), cancellationToken);
            return "Container stopped.";
        }, cancellationToken);

    public async Task<DockerActionResult> RestartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Restart", null, performedBy, async client =>
        {
            await client.Containers.RestartContainerAsync(containerName, new ContainerRestartParameters(), cancellationToken);
            return "Container restarted.";
        }, cancellationToken);

    public async Task<DockerActionResult> RemoveContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Remove", null, performedBy, async client =>
        {
            await client.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters(), cancellationToken);
            return "Container removed.";
        }, cancellationToken);

    public async Task<DockerActionResult> PullImageAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Pull", null, performedBy, async client =>
        {
            var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
            var image = inspect.Config?.Image ?? throw new InvalidOperationException("Container has no image reference.");
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image },
                new AuthConfig(),
                new Progress<JSONMessage>(),
                cancellationToken);
            return $"Pulled latest image for {image}. Use Recreate Container to apply it.";
        }, cancellationToken);

    public async Task<DockerActionResult> RecreateContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Recreate", null, performedBy, async client =>
        {
            var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
            var name = inspect.Name.TrimStart('/');

            await client.Containers.StopContainerAsync(containerName, new ContainerStopParameters(), cancellationToken);
            await client.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters(), cancellationToken);

            // Recreates from the container's own inspected Config/HostConfig. Custom
            // Docker networks beyond the default bridge aren't reattached (no
            // NetworkingConfig carried over) - acceptable for this app's own
            // restart-in-place use case; a full multi-network recreate is out of scope.
            var createParams = new CreateContainerParameters(inspect.Config)
            {
                Name = name,
                HostConfig = inspect.HostConfig
            };
            var created = await client.Containers.CreateContainerAsync(createParams, cancellationToken);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
            return "Container recreated from the freshly pulled image and started.";
        }, cancellationToken);

    public async Task<DockerActionResult> ExecCommandAsync(string containerName, string command, string performedBy, CancellationToken cancellationToken = default) =>
        await RunActionAsync(containerName, "Exec", command, performedBy, async client =>
        {
            var execCreate = await client.Exec.ExecCreateContainerAsync(containerName, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Cmd = ["/bin/sh", "-c", command]
            }, cancellationToken);

            using var stream = await client.Exec.StartWithConfigContainerExecAsync(
                execCreate.ID, new ContainerExecStartParameters(), cancellationToken);
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);
            var output = string.Join("\n", new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
        }, cancellationToken);

    public async Task<IReadOnlyList<DockerActionLogView>> ListActionLogsAsync(string? containerName, CancellationToken cancellationToken = default)
    {
        var query = dbContext.DockerActionLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            query = query.Where(a => a.ContainerName == containerName);
        }

        var logs = await query.ToListAsync(cancellationToken);
        return logs
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .Select(a => new DockerActionLogView
            {
                Id = a.Id,
                ContainerName = a.ContainerName,
                Action = a.Action,
                Command = a.Command,
                Succeeded = a.Succeeded,
                ResultSummary = a.ResultSummary,
                PerformedBy = a.PerformedBy,
                CreatedAt = a.CreatedAt
            })
            .ToList();
    }

    // Shared plumbing for every write action: opens a client, runs the action, and always
    // records a DockerActionLog row - success or failure - before returning.
    private async Task<DockerActionResult> RunActionAsync(
        string containerName,
        string action,
        string? command,
        string performedBy,
        Func<DockerClient, Task<string>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            var resultSummary = await operation(client);
            await LogActionAsync(containerName, action, command, true, Truncate(resultSummary), performedBy, cancellationToken);
            return new DockerActionResult(true, resultSummary);
        }
        catch (Exception ex) when (IsSocketUnavailable(ex) || ex is DockerContainerNotFoundException || ex is DockerApiException)
        {
            await LogActionAsync(containerName, action, command, false, Truncate(ex.Message), performedBy, cancellationToken);
            return new DockerActionResult(false, ex.Message);
        }
    }

    private async Task LogActionAsync(
        string containerName,
        string action,
        string? command,
        bool succeeded,
        string? resultSummary,
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

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length > 2000 ? value[..2000] : value;

    private static DockerClient CreateClient() =>
        new DockerClientConfiguration(new Uri(DockerSocketUri)).CreateClient();

    private static bool IsSocketUnavailable(Exception ex) =>
        ex is TimeoutException
            or DockerApiException
            or HttpRequestException
            or IOException
            or System.Net.Sockets.SocketException;

    private static ContainerHealthView MapToView(string name, ContainerInspectResponse inspect)
    {
        var state = inspect.State;
        var health = state?.Health?.Status ?? string.Empty;
        var restartCount = (int)inspect.RestartCount;
        var isError =
            string.Equals(health, "unhealthy", StringComparison.OrdinalIgnoreCase) ||
            (state?.Restarting ?? false) ||
            (state?.Dead ?? false) ||
            (state?.OOMKilled ?? false) ||
            (!(state?.Running ?? false) && (state?.ExitCode ?? 0) != 0);

        return new ContainerHealthView
        {
            Name = name,
            Image = inspect.Config?.Image ?? string.Empty,
            State = state?.Status ?? "unknown",
            Status = BuildStatusText(state),
            Health = string.IsNullOrWhiteSpace(health) ? "none" : health,
            StartedAt = ParseDockerTime(state?.StartedAt),
            FinishedAt = ParseDockerTime(state?.FinishedAt),
            RestartCount = restartCount,
            ExitCode = state?.ExitCode ?? 0,
            IsError = isError
        };
    }

    private static string BuildStatusText(ContainerState? state)
    {
        if (state is null) return "unknown";
        if (state.Running) return state.StartedAt is { } started ? $"Up since {started:u}" : "Running";
        if (state.Restarting) return "Restarting";
        if (state.Dead) return "Dead";
        return $"Exited ({state.ExitCode})";
    }

    private static DateTimeOffset? ParseDockerTime(string? value)
    {
        // Docker returns the zero value "0001-01-01T00:00:00Z" for timestamps that
        // haven't happened yet (e.g. FinishedAt on a container that's still running).
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, out var parsed) && parsed.Year > 1
            ? parsed
            : null;
    }

    // Static, instant, no external dependency - covers the handful of failure
    // patterns that actually show up on a small two-container droplet. An
    // Ollama-backed "diagnose further" option is a natural later add-on.
    // Public so it can be unit tested directly (same convention as
    // ContentStudioService.CreateSlug/BuildPrompt).
    public static string SuggestTip(ContainerHealthView view)
    {
        if (view.ExitCode == 137)
        {
            return "Exit code 137 usually means the container was killed for using too much memory (OOM). Check for a memory leak or raise the container's memory limit.";
        }

        if (string.Equals(view.Health, "unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return "The container's healthcheck is failing - the app inside probably isn't responding on its expected port. Check the log tail below for a crash or a slow startup.";
        }

        if (view.RestartCount >= 5)
        {
            return $"This container has restarted {view.RestartCount} times, which looks like a crash loop. Check the log tail below for the error that's happening right after each start.";
        }

        if (view.ExitCode != 0)
        {
            return $"Exited with a non-zero code ({view.ExitCode}), meaning the process inside crashed or exited with an error. Check the log tail below for the actual exception or error message.";
        }

        return "Check the log tail below for details.";
    }

    private static DockerHealthAlertView ToAlertView(DockerHealthAlert alert) => new()
    {
        Id = alert.Id,
        ContainerName = alert.ContainerName,
        Severity = alert.Severity,
        Message = alert.Message,
        IsRead = alert.IsRead,
        CreatedAt = alert.CreatedAt
    };
}
