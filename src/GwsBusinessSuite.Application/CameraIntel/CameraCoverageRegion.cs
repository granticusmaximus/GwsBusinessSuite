namespace GwsBusinessSuite.Application.CameraIntel;

// One entry in the Overwatch Grid coverage index - a "jump straight there" shortcut for a region
// with known live camera coverage. Deliberately a static, hand-maintained list rather than a
// live query: no ICameraFeedProvider/CameraDirectoryService method returns "all known regions"
// independent of a bounding box (every provider is queried bbox-first), and no provider exposes
// a fixed region name anywhere in its data - Datumfeed's own registries in particular are only
// ever known by whatever happens to come back from a live bbox query. Keep this in sync by hand
// whenever a new zero-registration source is added (see CameraDirectoryService's providers).
public sealed record CameraCoverageRegion(
    string Name,
    string SourceName,
    double Latitude,
    double Longitude,
    double FlyToHeightMeters);

public static class CameraCoverageRegions
{
    // Windy is deliberately excluded - its webcams are globally scattered, not a fixed region,
    // so it has no natural "fly here" entry; its cameras still surface organically wherever the
    // globe happens to be, unchanged.
    public static readonly IReadOnlyList<CameraCoverageRegion> All =
    [
        new("Georgia (GDOT)", "GDOT", 33.75, -84.39, 400_000),
        new("Washington State (WSDOT / Datumfeed)", "WSDOT", 47.60, -122.33, 400_000),
        new("Austin, TX (Datumfeed)", "Datumfeed", 30.27, -97.74, 150_000),
        new("California (Datumfeed / Caltrans)", "Datumfeed", 36.78, -119.42, 1_200_000),
        new("Ontario (Datumfeed / MTO)", "Datumfeed", 43.65, -79.38, 400_000),
        new("Ottawa (Datumfeed)", "Datumfeed", 45.42, -75.70, 150_000),
        new("Toronto (Datumfeed / RESCU)", "Datumfeed", 43.65, -79.38, 150_000),
        new("London (Datumfeed / JamCams)", "Datumfeed", 51.51, -0.13, 150_000),
    ];
}
