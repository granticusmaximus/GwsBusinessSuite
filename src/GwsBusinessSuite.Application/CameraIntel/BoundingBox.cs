namespace GwsBusinessSuite.Application.CameraIntel;

// The globe's current visible extent (degrees), computed client-side from the Cesium camera
// once it settles after a drag/zoom - see tactical-globe.js's moveEnd handler. Deliberately a
// plain rectangle, not a radius-from-center - matches what Cesium's own
// Camera.computeViewRectangle() returns with no reprojection needed.
public sealed record BoundingBox(double North, double South, double East, double West)
{
    public bool Contains(double latitude, double longitude) =>
        latitude <= North && latitude >= South &&
        (West <= East
            ? longitude >= West && longitude <= East
            : longitude >= West || longitude <= East);

    public bool Intersects(BoundingBox other) =>
        South <= other.North && North >= other.South &&
        LongitudeIntervals().Any(left => other.LongitudeIntervals().Any(right => left.Start <= right.End && left.End >= right.Start));

    private IReadOnlyList<(double Start, double End)> LongitudeIntervals() =>
        West <= East ? [(West, East)] : [(West, 180), (-180, East)];
}
