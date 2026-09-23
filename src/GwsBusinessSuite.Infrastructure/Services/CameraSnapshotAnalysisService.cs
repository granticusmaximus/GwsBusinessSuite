using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Settings;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class CameraSnapshotAnalysisService(
    HttpClient http,
    IOllamaService ollamaService,
    ISiteSettingsService siteSettingsService,
    ILogger<CameraSnapshotAnalysisService> logger) : ICameraSnapshotAnalysisService
{
    // Camera snapshots run far smaller than Notion's 25 MB import ceiling (NotionService.
    // DownloadFileAsync) - kept as its own constant since there's no shared cross-module
    // byte-ceiling constant to reuse.
    private const long MaxSnapshotBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromSeconds(90);

    private const string AnalysisPrompt =
        "Describe what's currently visible in this camera image in one or two plain-English " +
        "sentences, focused on traffic and weather conditions (e.g. \"light traffic, clear " +
        "skies\" or \"vehicle stopped in the right lane, otherwise clear\"). Do not speculate " +
        "beyond what's visible in the frame.";

    public async Task<CameraSnapshotAnalysisResult> AnalyzeAsync(CameraFeed camera, CancellationToken cancellationToken = default)
    {
        if (camera.StreamKind == CameraStreamKind.Hls)
        {
            return new CameraSnapshotAnalysisResult(false, "Live video analysis isn't supported yet - only still-image cameras can be analyzed.");
        }

        var settings = await siteSettingsService.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.VisionModelOverride))
        {
            return new CameraSnapshotAnalysisResult(false, "No vision model configured - set one in Settings > SentinelGPT.");
        }

        byte[] snapshotBytes;
        try
        {
            snapshotBytes = await DownloadSnapshotAsync(camera.StreamUrl, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by DownloadSnapshotAsync itself with an already user-friendly message
            // (invalid/non-HTTPS URL, oversized snapshot) - surface it directly rather than
            // flattening it into the generic network-failure message below.
            logger.LogWarning(ex, "Could not fetch snapshot for camera '{CameraId}'.", camera.Id);
            return new CameraSnapshotAnalysisResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch snapshot for camera '{CameraId}'.", camera.Id);
            return new CameraSnapshotAnalysisResult(false, "Could not fetch the camera's current snapshot.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AnalysisTimeout);
        try
        {
            var base64Image = Convert.ToBase64String(snapshotBytes);
            var response = await ollamaService.ChatAsync(
                settings.VisionModelOverride,
                [new OllamaChatMessage("user", AnalysisPrompt, Images: [base64Image])],
                ct: timeoutCts.Token);
            var description = response.Content.Trim();
            return string.IsNullOrWhiteSpace(description)
                ? new CameraSnapshotAnalysisResult(false, "The model returned an empty response.")
                : new CameraSnapshotAnalysisResult(true, description);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CameraSnapshotAnalysisResult(false, "Analysis timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Vision analysis failed for camera '{CameraId}' using model '{Model}'.", camera.Id, settings.VisionModelOverride);
            return new CameraSnapshotAnalysisResult(false, "Analysis failed - check that the configured vision model is installed.");
        }
    }

    // Mirrors NotionService.DownloadFileAsync's HTTPS-only + streamed-headers + hard byte
    // ceiling pattern - the first time this codebase fetches camera snapshot bytes server-side
    // (every CameraIntel provider only fetches metadata; the browser's own <img> tag fetches
    // pixels today). Every registered camera provider's base API is HTTPS-only (confirmed live
    // against GDOT's actual snapshot URL before writing this), so HTTPS-only is a safe default -
    // relax to allow HTTP too if a genuine HTTP-only camera source is ever added.
    private async Task<byte[]> DownloadSnapshotAsync(string snapshotUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(snapshotUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Camera snapshot URL is not a valid HTTPS URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxSnapshotBytes)
        {
            throw new InvalidOperationException("Camera snapshot exceeds the 10 MB analysis limit.");
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (content.Length > MaxSnapshotBytes)
        {
            throw new InvalidOperationException("Camera snapshot exceeds the 10 MB analysis limit.");
        }

        return content;
    }
}
