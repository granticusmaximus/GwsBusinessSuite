using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CameraIntel;

// Datumfeed (https://datumfeed.com) - a free, well-documented aggregator that health-polls and
// normalizes several official public traffic-camera registries into one bounding-box-queryable
// API. Verified directly against the live API and its published registry metadata
// (GET /api/registries) during implementation, not assumed:
//   - Real registries covered: Austin TX (ATD), Caltrans/California DOT, Ontario 511 (MTO),
//     City of Ottawa, City of Toronto (RESCU), Transport for London (JamCams), and - unless
//     WsdotTrafficCameraProvider already has its own AccessCode configured (see below) -
//     Washington State (WSDOT) too. Roughly 7,500-9,000 active cameras across 3 countries.
//   - Anonymous access already works with no key at all (60 req/h per IP); an optional free key
//     (self-registered via POST /api/keys) raises that to 3600 req/h - see CameraIntelOptions.
//     The user may not want to register for *any* key at all (a real, stated preference, not
//     just an unconfigured default) - Datumfeed's anonymous tier is the zero-registration path
//     to real Washington coverage in that case, since it re-publishes WSDOT's own cameras.
//   - `GET /api/cameras?bbox=west,south,east,north` returns a real bounding-box-filtered list;
//     confirmed against a live call during implementation.
//   - Every camera returned is a periodically-refreshed still image (`feedType: "jpeg_poll"`),
//     never a video stream, so this always maps to CameraStreamKind.Snapshot.
public sealed class DatumfeedCameraProvider(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    ILogger<DatumfeedCameraProvider> logger) : ICameraFeedProvider
{
    // Datumfeed's own registry slug for the WSDOT cameras it separately re-publishes. Only
    // skipped when WsdotTrafficCameraProvider is ALSO going to return cameras (i.e. a WSDOT
    // AccessCode is configured) - otherwise this is the only source of Washington coverage, and
    // filtering it out unconditionally would silently drop the state entirely for anyone who
    // declines to register for a WSDOT key.
    private const string WsdotRegistrySlug = "wsdot-cameras";

    // A safety cap, not a real limit Datumfeed enforces - keeps a single wide-zoomed view from
    // requesting (and the globe from having to render) an unbounded number of pins at once.
    private const int MaxResultsPerQuery = 200;

    public string SourceName => "Datumfeed";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"api/cameras?bbox={bbox.West.ToString(System.Globalization.CultureInfo.InvariantCulture)},{bbox.South.ToString(System.Globalization.CultureInfo.InvariantCulture)},{bbox.East.ToString(System.Globalization.CultureInfo.InvariantCulture)},{bbox.North.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit={MaxResultsPerQuery}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var apiKey = options.Value.DatumfeedApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var cameras = root?["cameras"]?.AsArray();
            if (cameras is null)
            {
                return [];
            }

            var skipWsdot = !string.IsNullOrWhiteSpace(options.Value.WsdotAccessCode);
            var results = new List<CameraFeed>();
            foreach (var camera in cameras)
            {
                var feed = TryParse(camera, skipWsdot);
                if (feed is not null)
                {
                    results.Add(feed);
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Datumfeed cameras.");
            return [];
        }
    }

    private static CameraFeed? TryParse(JsonNode? camera, bool skipWsdot)
    {
        var registrySlug = camera?["registry"]?["slug"]?.GetValue<string>();
        if (skipWsdot && string.Equals(registrySlug, WsdotRegistrySlug, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = camera?["id"]?.GetValue<string>();
        var lat = camera?["lat"]?.GetValue<double>();
        var lon = camera?["lon"]?.GetValue<double>();
        var feedUrl = camera?["feedUrl"]?.GetValue<string>();
        var active = camera?["active"]?.GetValue<bool>() ?? true;
        if (string.IsNullOrWhiteSpace(id) || lat is null || lon is null || string.IsNullOrWhiteSpace(feedUrl) || !active)
        {
            return null;
        }

        // commercialOk is true/false/null ("unknown") per registry - only an explicit false
        // (none of the 7 registries had this as of implementation, but a future one might) is
        // excluded; null/unknown still renders, same posture Datumfeed's own docs take toward it.
        var commercialOk = camera?["registry"]?["commercialOk"]?.GetValue<bool?>();
        if (commercialOk == false)
        {
            return null;
        }

        var name = camera?["name"]?.GetValue<string>();
        var registryName = camera?["registry"]?["name"]?.GetValue<string>();
        var attribution = camera?["registry"]?["attribution"]?.GetValue<string>();
        var licenseUrl = camera?["registry"]?["licenseUrl"]?.GetValue<string>();

        return new CameraFeed(
            Id: $"datumfeed-{id}",
            Name: string.IsNullOrWhiteSpace(name) ? id : name,
            Latitude: lat.Value,
            Longitude: lon.Value,
            StreamUrl: feedUrl,
            StreamKind: CameraStreamKind.Snapshot,
            // Credits the actual originating agency (e.g. "California Department of
            // Transportation (Caltrans)"), not just "Datumfeed" - honors Datumfeed's own stated
            // attribution requirement ("show registry.attribution next to any frame you render").
            SourceName: attribution ?? registryName ?? "Datumfeed",
            SourceAttributionUrl: licenseUrl ?? "https://datumfeed.com");
    }
}
