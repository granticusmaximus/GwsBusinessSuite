using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Application.RoadTraffic;

public sealed record BusyRoad(string Label, string? Name, string RoadClass, int VehiclesPerDay, double? TruckPercent, int? Lanes, int? SpeedLimitMph,
    double Latitude, double Longitude);

// The traffic picture for one view, from the HPMS sections drawn in it. Miles and vehicle-miles
// count only the parts of each section inside the view.
public sealed record RoadTrafficSummary(int Sections, double MilesInView, double VehicleMilesPerDay, double? TruckPercent,
    IReadOnlyList<BusyRoad> Busiest)
{
    public const int BusiestCount = 6;

    public static RoadTrafficSummary From(IReadOnlyList<RoadSegment> roads, BoundingBox view)
    {
        double miles = 0, vehicleMiles = 0, truckMiles = 0, vehicleMilesWithTrucks = 0;
        foreach (var road in roads)
        {
            var inView = MilesInside(road, view);
            miles += inView;
            vehicleMiles += inView * road.VehiclesPerDay;
            if (road.TrucksPerDay is { } trucks)
            {
                truckMiles += inView * trucks;
                vehicleMilesWithTrucks += inView * road.VehiclesPerDay;
            }
        }

        // A named road (I-75, Grand Cntrl Pkwy) is one road however many sections it has; an
        // unnamed one only has a class, so identical sections (both directions) count once.
        // Ramps carry a freeway's traffic, not their own, so they aren't listed.
        var busiest = roads
            .Where(r => !r.Ramp)
            .GroupBy(r => r.Named ? r.Label : $"#{r.Label}:{r.VehiclesPerDay}")
            .Select(g => g.MaxBy(r => r.VehiclesPerDay)!)
            .OrderByDescending(r => r.VehiclesPerDay)
            .Take(BusiestCount)
            .Select(r =>
            {
                var path = r.Paths[0];
                var (lon, lat) = path[path.Count / 2];
                return new BusyRoad(r.Label, r.Name == r.Label ? null : r.Name, r.RoadClass, r.VehiclesPerDay,
                    r.TrucksPerDay is { } t ? Math.Round(100.0 * t / r.VehiclesPerDay, 1) : null, r.Lanes, r.SpeedLimitMph, lat, lon);
            })
            .ToList();

        return new RoadTrafficSummary(roads.Count, miles, vehicleMiles,
            vehicleMilesWithTrucks > 0 ? Math.Round(100 * truckMiles / vehicleMilesWithTrucks, 1) : null, busiest);
    }

    // A piece of a path counts when its midpoint is in the view: close enough at these zooms.
    public static double MilesInside(RoadSegment road, BoundingBox view)
    {
        double total = 0;
        foreach (var path in road.Paths)
        for (var i = 1; i < path.Count; i++)
        {
            var (lon1, lat1) = path[i - 1];
            var (lon2, lat2) = path[i];
            if (view.Contains((lat1 + lat2) / 2, (lon1 + lon2) / 2)) total += Miles(lat1, lon1, lat2, lon2);
        }
        return total;
    }

    public static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthMiles = 3958.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthMiles * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

// Live speeds in the view: how many links, the typical speed, and the slowest stretches.
public sealed record LiveSpeedSummary(int Links, double MedianMph, int SlowLinks, IReadOnlyList<LiveRoadSpeed> Slowest, DateTimeOffset? Newest)
{
    // Under this is "slow" for the summary and red on the map.
    public const double SlowMph = 15;

    public static LiveSpeedSummary? From(IReadOnlyList<LiveRoadSpeed> links)
    {
        if (links.Count == 0) return null;
        var speeds = links.Select(l => l.SpeedMph).Order().ToList();
        var median = speeds.Count % 2 == 1 ? speeds[speeds.Count / 2] : (speeds[speeds.Count / 2 - 1] + speeds[speeds.Count / 2]) / 2;
        return new LiveSpeedSummary(links.Count, Math.Round(median, 1), links.Count(l => l.SpeedMph < SlowMph),
            links.OrderBy(l => l.SpeedMph).Take(3).ToList(), links.Max(l => l.AsOf));
    }
}
