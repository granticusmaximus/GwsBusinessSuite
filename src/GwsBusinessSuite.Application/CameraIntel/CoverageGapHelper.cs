namespace GwsBusinessSuite.Application.CameraIntel;

public sealed record CoverageSuggestion(CameraCoverageRegion Region, double Miles, string Direction);

// Overwatch's coverage-gap helper: when a view has no cameras, the nearest regions from the
// coverage index (CameraCoverageRegions), with how far and which way they are from the middle of
// the view. Regions centred inside the view are skipped - an empty view over a covered region
// means that source is quiet or down, and "go here" would just point back at the same spot.
public static class CoverageGapHelper
{
    private const double EarthRadiusMiles = 3958.8;
    private static readonly string[] Compass = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];

    public static IReadOnlyList<CoverageSuggestion> Nearest(BoundingBox view, IEnumerable<CameraCoverageRegion> regions, int count = 3)
    {
        var lat = (view.North + view.South) / 2;
        var lon = CentreLongitude(view);
        return regions
            .Where(r => !view.Contains(r.Latitude, r.Longitude))
            .Select(r => new CoverageSuggestion(r, Miles(lat, lon, r.Latitude, r.Longitude), Direction(lat, lon, r.Latitude, r.Longitude)))
            .OrderBy(s => s.Miles)
            .Take(count)
            .ToList();
    }

    public static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMiles * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // Initial great-circle bearing, as one of eight compass words.
    public static string Direction(double lat1, double lon1, double lat2, double lon2)
    {
        var y = Math.Sin(Radians(lon2 - lon1)) * Math.Cos(Radians(lat2));
        var x = Math.Cos(Radians(lat1)) * Math.Sin(Radians(lat2)) - Math.Sin(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Cos(Radians(lon2 - lon1));
        var bearing = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        return Compass[(int)Math.Round(bearing / 45) % 8];
    }

    // A view across the antimeridian has West > East.
    private static double CentreLongitude(BoundingBox view)
    {
        if (view.West <= view.East) return (view.West + view.East) / 2;
        var centre = (view.West + view.East + 360) / 2;
        return centre > 180 ? centre - 360 : centre;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
