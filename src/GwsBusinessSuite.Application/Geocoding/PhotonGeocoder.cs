using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Geocoding;

public sealed class PhotonGeocoder(HttpClient httpClient, ILogger<PhotonGeocoder> logger) : IPhotonGeocoder
{
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
}
