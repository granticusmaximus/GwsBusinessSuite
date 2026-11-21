using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.DockerHealth;

namespace GwsBusinessSuite.Application.DigitalOcean;

public interface IDropletRemoteDockerService
{
    Task<DropletRemoteInspectionResult> InspectDropletAsync(ActiveDropletView droplet, CancellationToken cancellationToken = default);

    Task<DropletRemoteLogsResult> TailContainerLogsAsync(ActiveDropletView droplet, string containerName, int tailLines = 100, CancellationToken cancellationToken = default);

    Task<DockerActionResult> PullImageAsync(ActiveDropletView droplet, string image, string performedBy, CancellationToken cancellationToken = default);

    Task<DockerActionResult> RestartContainerAsync(ActiveDropletView droplet, string containerName, string performedBy, CancellationToken cancellationToken = default);

    Task<DockerActionResult> PruneDanglingImagesAsync(ActiveDropletView droplet, string performedBy, CancellationToken cancellationToken = default);

    Task<DockerActionResult> PruneStaleImagesAsync(ActiveDropletView droplet, string performedBy, CancellationToken cancellationToken = default);

    Task<DockerActionResult> RestartOllamaAsync(ActiveDropletView droplet, string performedBy, CancellationToken cancellationToken = default);
}

public sealed record DropletCachedImageView(
    string Repository,
    string Tag,
    string ImageId,
    string Size,
    string CreatedAt)
{
    public string FullName => string.Equals(Tag, "<none>", StringComparison.Ordinal) || string.IsNullOrEmpty(Tag)
        ? Repository
        : $"{Repository}:{Tag}";

    public bool IsDangling => string.Equals(Repository, "<none>", StringComparison.Ordinal)
        || string.Equals(Tag, "<none>", StringComparison.Ordinal);
}

public sealed record DropletDockerDiskUsageView(
    string Type,
    string TotalCount,
    string ActiveCount,
    string Size,
    string Reclaimable);

public sealed record DropletOllamaModelView(
    string Name,
    string ModelId,
    string Size,
    string Modified);

public sealed record DropletAiRuntimeView(
    bool Installed,
    string OllamaServiceState,
    string WatchdogTimerState,
    IReadOnlyList<DropletOllamaModelView> Models);

public sealed record DropletRemoteInspectionResult(
    bool Succeeded,
    IReadOnlyList<DropletCachedImageView> CachedImages,
    IReadOnlyList<DropletDockerDiskUsageView> DiskUsage,
    DropletAiRuntimeView? AiRuntime,
    string? ErrorMessage = null);

public sealed record DropletRemoteLogsResult(
    bool Succeeded,
    string Logs,
    string? ErrorMessage = null);

public static partial class DropletDockerIdentifier
{
    private const int MaxLength = 256;

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && IdentifierRegex().IsMatch(value);

    [GeneratedRegex("^[a-zA-Z0-9._:/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
