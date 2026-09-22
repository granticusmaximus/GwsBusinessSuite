using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Geocoding;

public sealed class PhotonGeocoder(HttpClient httpClient, ILogger<PhotonGeocoder> logger) : IPhotonGeocoder
{
    // Photon's reverse endpoint always returns its single nearest indexed feature, however far
    // away that actually is - hovering over a field with nothing mapped nearby would otherwise
    // silently label it with, say, a town center a mile away. Reject anything farther than this
    // rather than show a misleading answer.
    private const double MaxReverseGeocodeDistanceMeters = 150;

    public async Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"api/?limit=1&q={Uri.EscapeDataString(query)}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var coordinates = root?["features"]?.AsArray().FirstOrDefault()?["geometry"]?["coordinates"]?.AsArray();
            if (coordinates is null || coordinates.Count < 2)
            {
                return null;
            }

            // GeoJSON order is [longitude, latitude].
            return new GeocodeResult(coordinates[1]!.GetValue<double>(), coordinates[0]!.GetValue<double>());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Photon geocoding failed for query {Query}.", query);
            return null;
        }
    }

    public async Task<PlaceInfo?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        try
        {
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var response = await httpClient.GetAsync($"reverse?lat={lat}&lon={lon}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var feature = root?["features"]?.AsArray().FirstOrDefault();
            var coordinates = feature?["geometry"]?["coordinates"]?.AsArray();
            if (coordinates is null || coordinates.Count < 2)
            {
                return null;
            }

            var featureLon = coordinates[0]!.GetValue<double>();
            var featureLat = coordinates[1]!.GetValue<double>();
            if (DistanceMeters(latitude, longitude, featureLat, featureLon) > MaxReverseGeocodeDistanceMeters)
            {
                return null;
            }

            var properties = feature?["properties"];
            var name = properties?["name"]?.GetValue<string>();
            var houseNumber = properties?["housenumber"]?.GetValue<string>();
            var street = properties?["street"]?.GetValue<string>();
            var city = properties?["city"]?.GetValue<string>();
            var state = properties?["state"]?.GetValue<string>();
            var category = properties?["osm_value"]?.GetValue<string>();

            var streetAddress = houseNumber is not null && street is not null ? $"{houseNumber} {street}" : street;
            var displayName = name ?? streetAddress ?? category ?? "Unnamed location";
            var address = string.Join(", ", new[] { streetAddress, city, state }.Where(part => !string.IsNullOrWhiteSpace(part)));

            return new PlaceInfo(
                DisplayName: displayName,
                Category: category != displayName ? category : null,
                Address: string.IsNullOrWhiteSpace(address) ? null : address);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Photon reverse geocoding failed for {Latitude},{Longitude}.", latitude, longitude);
            return null;
        }
    }

    // Haversine - accurate enough for a ~150m proximity check, no need for anything more precise.
    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6371000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2))
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
