using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Application.TrafficIncidents;

public interface ITrafficIncidentProvider
{
    string SourceName { get; }

    Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default);
}
