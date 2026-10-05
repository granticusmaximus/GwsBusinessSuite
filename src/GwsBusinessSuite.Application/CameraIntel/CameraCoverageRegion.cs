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
        new("Delaware (DelDOT)", "DelDOT", 39.00, -75.50, 250_000),
        new("Rhode Island (RIDOT)", "RIDOT", 41.70, -71.50, 150_000),
        new("Virginia (VDOT)", "VDOT", 37.50, -78.60, 700_000),
        new("Kentucky (KYTC)", "KYTC", 37.80, -85.00, 600_000),
        new("South Carolina (SCDOT)", "SCDOT", 33.90, -80.90, 600_000),
        new("Missouri (MoDOT)", "MoDOT", 38.50, -92.50, 700_000),
        new("Michigan (MDOT MiDrive)", "MDOT MiDrive", 43.30, -84.50, 700_000),
        new("North Dakota (NDDOT)", "NDDOT", 47.50, -100.50, 600_000),
        // Colorado (CDOT) removed 2026-09-29: its camera source is disabled in DI (see
        // DependencyInjection.cs) because cotrip.org's image endpoint was retired.
        new("Hawaii - Oahu (HDOT)", "HDOT", 21.45, -157.98, 150_000),
        new("Texas (TxDOT)", "TxDOT", 31.00, -99.00, 1_400_000),
        new("Alabama (ALDOT)", "ALDOT", 32.80, -86.80, 600_000),
        new("Tennessee (TDOT SmartWay Incidents)", "TDOT SmartWay", 35.90, -86.40, 600_000),
        new("New York Thruway (Incidents)", "NY Thruway", 43.00, -76.00, 700_000),
        new("Maine (511ME Incidents)", "511ME", 45.30, -69.20, 500_000),
        new("New Hampshire (511NH Incidents)", "511NH", 43.90, -71.50, 300_000),
        new("Vermont (511VT Incidents)", "511VT", 44.00, -72.70, 300_000),
        new("Arizona (AZ511 WZDx Incidents)", "AZ511 WZDx", 34.20, -111.90, 700_000),
        new("Nevada (NVRoads WZDx Incidents)", "NVRoads WZDx", 39.30, -116.60, 700_000),
        new("Utah (UDOT WZDx Incidents)", "UDOT WZDx", 39.30, -111.70, 600_000),
        new("Oklahoma (OK Traffic WZDx Incidents)", "OK Traffic WZDx", 35.50, -97.50, 600_000),
        new("Idaho (Idaho 511 WZDx Incidents)", "Idaho 511 WZDx", 44.50, -114.50, 700_000),
        // Second WZDx batch (2026-10-04): states with no other entry here. FL, WA, KY, MO, DE
        // and Austin also gained work-zone incidents but are already listed for cameras.
        new("Maryland (MDOT WZDx Incidents)", "MDOT WZDx", 39.00, -76.80, 300_000),
        new("Wisconsin (511WI WZDx Incidents)", "511WI WZDx", 44.60, -89.90, 500_000),
        new("New Jersey (Smart Work Zones WZDx Incidents)", "NJ Smart Work Zones WZDx", 40.10, -74.60, 250_000),
        new("Kansas (KanDrive WZDx Incidents)", "KanDrive WZDx", 38.50, -98.40, 600_000),
        new("Indiana (INDOT WZDx Incidents)", "INDOT WZDx", 39.90, -86.30, 450_000),
        new("North Carolina (DriveNC WZDx Incidents)", "DriveNC WZDx", 35.50, -79.40, 600_000),
        new("Mississippi (MDOT Traffic WZDx Incidents)", "MDOT Traffic WZDx", 32.70, -89.70, 450_000),
        new("Louisiana (LA DOTD WZDx Incidents)", "LA DOTD WZDx", 31.00, -92.00, 450_000),
        new("British Columbia (DriveBC)", "DriveBC", 53.00, -122.00, 1_600_000),
        new("Madrid, Spain", "Madrid Traffic", 40.42, -3.70, 150_000),
        new("Finland (Fintraffic)", "Fintraffic", 64.50, 26.00, 1_800_000),
    ];
}
