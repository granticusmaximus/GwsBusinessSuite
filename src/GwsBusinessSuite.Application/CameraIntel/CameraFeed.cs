namespace GwsBusinessSuite.Application.CameraIntel;

// A single publicly-viewable camera pin on the Tactical Globe. Only ever populated from a
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
// client appends its own cache-busting query param). Hls is for a future source that serves a
// real HLS manifest, reusing the same hls.js-based playback already established for Civic
// Watch's floor-video feature (see civicWatchVideo.js) - no pilot source below produces this
// yet, but the render path is written to support it now rather than bolted on later.
public enum CameraStreamKind
{
    Snapshot,
    Hls
}
