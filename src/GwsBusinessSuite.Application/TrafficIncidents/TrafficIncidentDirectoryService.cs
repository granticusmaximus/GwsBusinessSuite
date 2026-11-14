using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// Mirrors CameraDirectoryService exactly - fans out to every registered ITrafficIncidentProvider
// in parallel, isolating one misbehaving source from taking down every other source's incidents.
public sealed class TrafficIncidentDirectoryService(
    IEnumerable<ITrafficIncidentProvider> providers,
    ILogger<TrafficIncidentDirectoryService> logger,
    SourceStatusTracker? statusTracker = null,
    IIncidentHistoryRecorder? history = null)
{
    public TimeSpan Deadline { get; init; } = SourceDeadline.Default;

    public async Task<IReadOnlyList<TrafficIncident>> GetIncidentsInBoundingBoxAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var loads = providers.Select(provider => (provider.SourceName, FetchSafelyAsync(provider, bbox, cancellationToken))).ToList();
        // What has arrived by the deadline; a slow source no longer holds every other one (SourceDeadline).
        var results = await SourceDeadline.GatherAsync(loads, Deadline, logger, "incidents", cancellationToken);
        var incidents = results.SelectMany(incidents => incidents).Select(IncidentNormalizer.Normalize).ToList();
        // Overwatch's time machine remembers what was seen (in memory; written out in the background).
        history?.Record(incidents);
        return incidents;
    }

    private async Task<IReadOnlyList<TrafficIncident>> FetchSafelyAsync(ITrafficIncidentProvider provider, BoundingBox bbox, CancellationToken cancellationToken)
    {
        try
        {
            var incidents = await provider.GetIncidentsAsync(bbox, cancellationToken);
            statusTracker?.RecordSuccess(provider.SourceName, SourceStatusTracker.IncidentKind, incidents.Count);
            return incidents;
        }
        catch (Exception ex)
        {
            statusTracker?.RecordFailure(provider.SourceName, SourceStatusTracker.IncidentKind, ex.GetType().Name);
            logger.LogWarning(ex, "Traffic incident provider {SourceName} failed; continuing with the other sources.", provider.SourceName);
            return [];
        }
    }
}
