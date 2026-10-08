namespace GwsBusinessSuite.Application.CameraIntel;

// A single publicly-viewable camera pin on the Overwatch. Only ever populated from a
// legitimate, publicly-provided source (a government open-data traffic camera API, an official
// public webcam aggregator) - see ICameraFeedProvider's own doc comment for the hard line this
// module holds on never scanning for or aggregating unsecured private cameras.
public sealed record CameraFeed(
    string Id,
    string Name,
    double Latitude,
    double Longitude,
    string StreamUrl,
    CameraStreamKind StreamKind,
    string SourceName,
    string SourceAttributionUrl);

// Most public traffic-camera APIs (WSDOT, Windy, etc.) serve a periodically-refreshed still
// image rather than continuous video - Snapshot means "re-fetch StreamUrl on an interval" (the
// client appends its own cache-busting query param). Hls is for sources that serve a real HLS
// manifest (including Norway, Missouri and Delaware); the globe uses its hls.js playback path.
public enum CameraStreamKind
{
    Snapshot,
    Hls
}
