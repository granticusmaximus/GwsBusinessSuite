using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Luxembourg's motorway cameras from CITA (the national traffic centre), published on
// data.public.lu under CC0 as one KML file. Verified live 07-11-26: 96 cameras. Each placemark's
// description frames CITA's viewer (webcam/map/{n}), which shows info_trafic/cameras/images/
// cccam_{n}.jpg; that JPEG is what's shown here (checked for several cameras).
public sealed partial class LuxembourgCitaCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<LuxembourgCitaCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:cita:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    public string SourceName => "CITA";

    public BoundingBox? Coverage { get; } = new(50.2, 49.4, 6.6, 5.7);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            if (allCameras.Count > 0) cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var kml = await httpClient.GetStringAsync("kml/cameras.kml", cancellationToken);
            var document = XDocument.Parse(kml);
            var cameras = new List<CameraFeed>();
            foreach (var placemark in document.Descendants().Where(element => element.Name.LocalName == "Placemark"))
            {
                var description = Child(placemark, "description")?.Value ?? string.Empty;
                var coordinates = placemark.Descendants().FirstOrDefault(element => element.Name.LocalName == "coordinates")?.Value.Trim().Split(',');
                if (Viewer().Match(description) is not { Success: true } viewer || coordinates is not { Length: >= 2 }
                    || !double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                    || !double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                    || latitude is < 49.3 or > 50.3 || longitude is < 5.6 or > 6.7) continue;
                var number = viewer.Groups[1].Value;
                var name = Child(placemark, "name")?.Value.Trim();
                cameras.Add(new CameraFeed(
                    Id: $"cita-{number}",
                    Name: $"Luxembourg: {(string.IsNullOrEmpty(name) ? $"Camera {number}" : name)}",
                    Latitude: latitude,
                    Longitude: longitude,
                    StreamUrl: $"https://www.cita.lu/info_trafic/cameras/images/cccam_{number}.jpg",
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://data.public.lu/fr/datasets/cita-cameras-autoroute/"));
            }
            return cameras.GroupBy(camera => camera.Id).Select(group => group.First()).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch CITA motorway cameras.");
            return [];
        }
    }

    private static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(child => child.Name.LocalName == name);

    // CITA's viewer for camera n: https://www.cita.lu/webcam/map/{n}
    [GeneratedRegex(@"cita\.lu/webcam/map/(\d{1,6})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Viewer();
}
