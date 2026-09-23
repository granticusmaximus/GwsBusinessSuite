namespace GwsBusinessSuite.Application.Weather;

// Point-in-polygon test for WeatherAlert.Rings ([exterior ring, then hole rings, ...] - the same
// convention GeoJSON MultiPolygon/Polygon-with-holes uses). Uses the standard crossing-number
// (ray-casting) algorithm per ring, XORed across rings: a point inside the exterior ring but also
// inside a hole ring ends up counted twice and correctly reads as outside. This is
// winding-order-agnostic by construction, since NWS's own GeoJSON gives no enforced guarantee
// either way. No antimeridian handling - matches BoundingBox's own accepted gap, since every
// pilot camera source (GDOT, Datumfeed, WSDOT) is CONUS-only.
public static class GeoPolygon
{
    public static bool Contains(
        IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>> rings,
        double latitude,
        double longitude)
    {
        var inside = false;
        foreach (var ring in rings)
        {
            if (RingContains(ring, latitude, longitude))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool RingContains(
        IReadOnlyList<(double Longitude, double Latitude)> ring, double latitude, double longitude)
    {
        if (ring.Count < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            var crosses = (yi > latitude) != (yj > latitude);
            if (crosses && longitude < ((xj - xi) * (latitude - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
