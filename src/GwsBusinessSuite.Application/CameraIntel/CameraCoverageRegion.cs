namespace GwsBusinessSuite.Application.CameraIntel;

// One entry in the Overwatch coverage index - a "jump straight there" shortcut for a region
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
        new("Florida (FL511)", "FL511", 28.10, -81.60, 900_000),
        new("Illinois (IDOT)", "IDOT", 40.00, -89.20, 700_000),
        new("Iowa (Iowa DOT)", "Iowa DOT", 41.90, -93.60, 500_000),
        new("Oregon (ODOT / TripCheck)", "ODOT", 44.00, -120.50, 600_000),
        new("New York City (NYC DOT)", "NYC DOT", 40.71, -74.01, 150_000),
        new("Minnesota (511MN Incidents)", "511MN", 46.30, -94.30, 500_000),
        new("Nebraska (511NE Incidents)", "511NE", 41.50, -99.90, 500_000),
        new("New Zealand (NZTA)", "NZTA", -41.00, 174.80, 1_800_000),
        new("Queensland, Australia (QLD Traffic)", "QLD Traffic", -23.00, 144.00, 2_200_000),
    ];
}
