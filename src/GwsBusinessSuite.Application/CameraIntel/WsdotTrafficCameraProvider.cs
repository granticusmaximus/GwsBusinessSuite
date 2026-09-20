using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CameraIntel;

// Washington State DOT's Traveler Information API - a real, publicly documented, free
// (self-registered AccessCode) government traffic camera feed. https://wsdot.wa.gov/traffic/api/
// Field names below are taken directly from WSDOT's own Camera class reference
// (wsdot.wa.gov/traffic/api/Documentation/class_camera.html), not guessed. Cameras are
// periodically-refreshed still images (ImageURL), not video - see CameraStreamKind.Snapshot.
public sealed class WsdotTrafficCameraProvider(
    HttpClient httpClient,
    IOptions<CameraIntelOptions> options,
    IMemoryCache cache,
    ILogger<WsdotTrafficCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:wsdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "WSDOT";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var accessCode = options.Value.WsdotAccessCode;
        if (string.IsNullOrWhiteSpace(accessCode))
        {
            return [];
        }

        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(accessCode, cancellationToken);
            cache.Set(CacheKey, allCameras, CacheDuration);
        }

        // WSDOT's API has no bounding-box query parameter - it returns every camera statewide
        // (a few hundred), cheap to filter in memory rather than worth a second cache axis.
        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(string accessCode, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"HighwayCameras/HighwayCamerasREST.svc/GetCamerasAsJson?AccessCode={Uri.EscapeDataString(accessCode)}";
            var dtos = await httpClient.GetFromJsonAsync<List<WsdotCameraDto>>(url, cancellationToken);
            if (dtos is null)
            {
                return [];
            }

            return dtos
                .Where(dto => dto is { IsActive: true, DisplayLatitude: not 0, DisplayLongitude: not 0 }
                    && !string.IsNullOrWhiteSpace(dto.ImageURL))
                .Select(dto => new CameraFeed(
                    Id: $"wsdot-{dto.CameraID}",
                    Name: string.IsNullOrWhiteSpace(dto.Title) ? $"WSDOT Camera {dto.CameraID}" : dto.Title,
                    Latitude: dto.DisplayLatitude,
                    Longitude: dto.DisplayLongitude,
                    StreamUrl: dto.ImageURL!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://wsdot.wa.gov/traffic/"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A source being unreachable must never take down every other provider's cameras -
            // CameraDirectoryService aggregates all providers independently (see its own comment).
            logger.LogWarning(ex, "Failed to fetch WSDOT traffic cameras.");
            return [];
        }
    }

    private sealed record WsdotCameraDto(
        int CameraID,
        string? Title,
        double DisplayLatitude,
        double DisplayLongitude,
        string? ImageURL,
        bool IsActive);
}
