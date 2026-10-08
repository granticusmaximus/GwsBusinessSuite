using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Madrid City Council's traffic camera feed (informo.madrid.es) - genuinely free with no API key,
// confirmed directly (a real Camara{Numero}.jpg resolved live, HTTP 200 image/jpeg, no Referer
// check observed). Published as KML, not JSON - each Placemark carries its camera number/name in
// ExtendedData rather than as plain element text, and its coordinates in KML's own
// longitude,latitude[,altitude] order (not GeoJSON's [longitude, latitude] array, but the same
// axis order) inside a single comma-separated string. The direct image URL
// (informo.madrid.es/cameras/Camara{Numero}.jpg) is a known pattern rather than a field in the
// KML itself - confirmed by extracting it from each Placemark's own embedded <description> HTML,
// but built directly from Numero here rather than regex-scraping the description string.
public sealed class MadridCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<MadridCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:madrid:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly XNamespace Kml = "http://earth.google.com/kml/2.2";

    public string SourceName => "Madrid Traffic";

    public BoundingBox? Coverage { get; } = new(40.7, 40.2, -3.4, -4.0);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var kml = await httpClient.GetStringAsync("informo/tmadrid/CCTV.kml", cancellationToken);
            var doc = XDocument.Parse(kml);
            var results = new List<CameraFeed>();

            foreach (var placemark in doc.Descendants(Kml + "Placemark"))
            {
                var numero = placemark.Descendants(Kml + "Data")
                    .FirstOrDefault(d => (string?)d.Attribute("name") == "Numero")?
                    .Element(Kml + "Value")?.Value;
                var nombre = placemark.Descendants(Kml + "Data")
                    .FirstOrDefault(d => (string?)d.Attribute("name") == "Nombre")?
                    .Element(Kml + "Value")?.Value;
                var coordinates = placemark.Descendants(Kml + "coordinates").FirstOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(numero) || string.IsNullOrWhiteSpace(coordinates)) continue;

                var parts = coordinates.Trim().Split(',');
                if (parts.Length < 2) continue;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;

                results.Add(new CameraFeed(
                    Id: $"madrid-{numero}",
                    Name: string.IsNullOrWhiteSpace(nombre) ? $"Madrid Camera {numero}" : $"Madrid: {nombre}",
                    Latitude: lat,
                    Longitude: lon,
                    StreamUrl: $"https://informo.madrid.es/cameras/Camara{numero}.jpg",
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://informo.madrid.es"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            logger.LogWarning(ex, "Failed to fetch Madrid traffic cameras.");
            return [];
        }
    }
}
