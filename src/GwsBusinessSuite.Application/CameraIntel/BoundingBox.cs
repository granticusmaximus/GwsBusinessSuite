namespace GwsBusinessSuite.Application.CameraIntel;

// The globe's current visible extent (degrees), computed client-side from the Cesium camera
// once it settles after a drag/zoom - see tactical-globe.js's moveEnd handler. Deliberately a
// plain rectangle, not a radius-from-center - matches what Cesium's own
// Camera.computeViewRectangle() returns with no reprojection needed.
public sealed record BoundingBox(double North, double South, double East, double West)
{
    // Doesn't handle an antimeridian-crossing view (West > East, e.g. centered near +/-180deg
    // longitude) - an acceptable v1 gap since no pilot provider has cameras anywhere near there.
    public bool Contains(double latitude, double longitude) =>
        latitude <= North && latitude >= South && longitude <= East && longitude >= West;
}
