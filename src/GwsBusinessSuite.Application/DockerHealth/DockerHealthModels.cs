namespace GwsBusinessSuite.Application.DockerHealth;

public sealed class ContainerHealthView
{
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Health { get; init; } = string.Empty;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int RestartCount { get; init; }
    public long ExitCode { get; init; }
    public bool IsError { get; init; }
}

// Returned instead of throwing when the Docker socket isn't reachable (e.g. local dev
// without it mounted) so the admin page can show a clear message instead of crashing.
public sealed record ContainerListResult(
    bool Available,
    IReadOnlyList<ContainerHealthView> Containers,
    string? UnavailableReason = null);

public sealed class ContainerDetailView
{
    public ContainerHealthView? Container { get; init; }
    public string LogTail { get; init; } = string.Empty;
    public string? SuggestedTip { get; init; }
    public IReadOnlyList<DockerHealthAlertView> RecentAlerts { get; init; } = Array.Empty<DockerHealthAlertView>();
}

public sealed class DockerHealthAlertView
{
    public Guid Id { get; init; }
    public string ContainerName { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsRead { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// Returned from every write action instead of throwing, so callers (Blazor pages) can
// show the outcome inline without a try/catch around every button handler.
public sealed record DockerActionResult(bool Succeeded, string Message);

public sealed class DockerActionLogView
{
    public Guid Id { get; init; }
    public string ContainerName { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? Command { get; init; }
    public bool Succeeded { get; init; }
    public string? ResultSummary { get; init; }
    public string PerformedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}
