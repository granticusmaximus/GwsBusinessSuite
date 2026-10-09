namespace GwsBusinessSuite.Infrastructure.Services;

// Fetches one still frame for a vision model ("ask the cameras"). Same rules as
// CameraSnapshotAnalysisService's own download: HTTPS only, and a byte ceiling - lower here,
// since each frame is then sent to the browser to reach the Mac's local Ollama.
public sealed class CameraSnapshotDownloader(HttpClient http)
{
    public const long MaxSnapshotBytes = 3 * 1024 * 1024;

    // Null when the frame can't be fetched or isn't an image; the caller reports it as unreadable.
    public async Task<byte[]?> DownloadAsync(string snapshotUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(snapshotUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxSnapshotBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length is > 0 and <= (int)MaxSnapshotBytes && IsImage(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsImage(byte[] bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => true,
        [0x89, 0x50, 0x4E, 0x47, ..] => true,
        [0x47, 0x49, 0x46, 0x38, ..] => true,
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => true,
        _ => false
    };
}
