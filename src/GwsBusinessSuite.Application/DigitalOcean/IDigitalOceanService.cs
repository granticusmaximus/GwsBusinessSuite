namespace GwsBusinessSuite.Application.DigitalOcean;

public interface IDigitalOceanService
{
    Task<DigitalOceanSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(DigitalOceanSettingsView settings, CancellationToken cancellationToken = default);

    Task<DropletInfoResult> GetDropletInfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DropletActionView>> ListRecentActionsAsync(CancellationToken cancellationToken = default);

    Task<DigitalOceanActionResult> RebootDropletAsync(string performedBy, CancellationToken cancellationToken = default);

    Task<DigitalOceanActionResult> ResizeDropletAsync(string newSize, bool resizeDisk, string performedBy, CancellationToken cancellationToken = default);

    Task<DigitalOceanActionResult> CreateSnapshotAsync(string snapshotName, string performedBy, CancellationToken cancellationToken = default);
}
