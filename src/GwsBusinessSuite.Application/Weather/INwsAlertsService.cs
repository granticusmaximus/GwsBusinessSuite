using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Application.Weather;

public interface INwsAlertsService
{
    // NWS's own /alerts/active endpoint has no rectangular bounding-box query parameter (only
    // area/zone/point/region) - this fetches the small nationwide active-alert set (a few
    // hundred, cheap) and filters to alerts with at least one vertex inside bbox itself, an
    // approximation that's more than accurate enough for "show alerts roughly where I'm looking."
    Task<IReadOnlyList<WeatherAlert>> GetActiveAlertsAsync(BoundingBox bbox, CancellationToken cancellationToken = default);
}
