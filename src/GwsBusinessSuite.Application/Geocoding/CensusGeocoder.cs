using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Geocoding;

public sealed class CensusGeocoder(HttpClient httpClient, ILogger<CensusGeocoder> logger) : ICensusGeocoder
{
    public async Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync(
                $"geocoder/locations/onelineaddress?address={Uri.EscapeDataString(query)}&benchmark=Public_AR_Current&format=json",
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var match = root?["result"]?["addressMatches"]?.AsArray().FirstOrDefault();
            var lat = match?["coordinates"]?["y"];
            var lon = match?["coordinates"]?["x"];
            if (lat is null || lon is null)
            {
                return null;
            }

            return new GeocodeResult(lat.GetValue<double>(), lon.GetValue<double>());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Census geocoding failed for query {Query}.", query);
            return null;
        }
    }
}
