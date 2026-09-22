using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// Mirrors CameraDirectoryService exactly - fans out to every registered ITrafficIncidentProvider
// in parallel, isolating one misbehaving source from taking down every other source's incidents.
public sealed class TrafficIncidentDirectoryService(
    IEnumerable<ITrafficIncidentProvider> providers,
    ILogger<TrafficIncidentDirectoryService> logger)
{
    public async Task<IReadOnlyList<TrafficIncident>> GetIncidentsInBoundingBoxAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var tasks = providers.Select(provider => FetchSafelyAsync(provider, bbox, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(incidents => incidents).ToList();
    }

    private async Task<IReadOnlyList<TrafficIncident>> FetchSafelyAsync(ITrafficIncidentProvider provider, BoundingBox bbox, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetIncidentsAsync(bbox, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Traffic incident provider {SourceName} failed; continuing with the other sources.", provider.SourceName);
            return [];
        }
    }
}
