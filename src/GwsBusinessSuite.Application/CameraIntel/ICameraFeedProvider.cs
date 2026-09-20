namespace GwsBusinessSuite.Application.CameraIntel;

// One implementation per legitimate public camera data source (a government open-data traffic
// camera API, an official public webcam aggregator). This is a hard boundary, not just a
// starting scope: a provider must only ever surface feeds the source itself publishes for
// public viewing. Never scan for, index, or aggregate unsecured private/residential cameras
// (the "Insecam" pattern) - that crosses into unauthorized-access/privacy-violation territory
// this module will not go near.
//
// Isolating each source behind its own provider means adding region #4 later is a new class
// implementing this interface, not a change to CameraDirectoryService or anything upstream of it.
public interface ICameraFeedProvider
{
    // A short, stable identifier used as CameraFeed.SourceName and for per-provider caching/
    // attribution - e.g. "WSDOT", "Windy".
    string SourceName { get; }

    // Returns every camera this source has within (or overlapping) the given bounding box.
    // Implementations that can't filter server-side (most small per-region APIs return their
    // whole list) fetch everything and let CameraDirectoryService apply the bbox filter - see
    // its own comment for why that's centralized there instead of duplicated per provider.
    Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default);
}
