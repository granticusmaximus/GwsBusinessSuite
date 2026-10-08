using System.Globalization;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record CameraReportResult(bool Saved, Guid? PageId, string Message);

// Overwatch "SAVE REPORT": turns what a camera shows right now into a Sentinel page - the frame
// itself (copied into the media library, because the camera's URL will show a different picture
// a minute later), where and when, and SentinelGPT's ANALYZE notes when there are any. Pages are
// filed under one "Overwatch reports" parent so they're easy to find and share.
public sealed class CameraIncidentReportService(
    HttpClient httpClient,
    IAppDbContextFactory dbContextFactory,
    IWikiService wikiService,
    IMediaLibraryService mediaLibrary,
    TimeProvider timeProvider,
    ILogger<CameraIncidentReportService> logger)
{
    public const string ReportsParentTitle = "Overwatch reports";
    private const long MaxSnapshotBytes = 8 * 1024 * 1024;

    public async Task<CameraReportResult> SaveReportAsync(CameraFeed camera, string? analysis, string username, CancellationToken ct = default)
    {
        var capturedAt = timeProvider.GetUtcNow();
        string? imageUrl = null;
        string? imageNote = null;
        if (camera.StreamKind == CameraStreamKind.Snapshot)
        {
            var bytes = await TryDownloadAsync(camera.StreamUrl, ct);
            if (bytes is null)
            {
                imageNote = "The camera's image couldn't be downloaded when this report was saved.";
            }
            else
            {
                try
                {
                    var fileName = $"overwatch-{Slug(camera.Id)}-{capturedAt:yyyyMMdd-HHmmss}.jpg";
                    var asset = await mediaLibrary.UploadAsync(fileName, bytes, $"{camera.Name} at {capturedAt:u}", ct);
                    imageUrl = asset.Url;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // The media library rejects anything that isn't a real image (by its bytes).
                    logger.LogWarning(ex, "Overwatch report: snapshot for {Camera} wasn't a storable image", camera.Id);
                    imageNote = "The camera returned something that isn't an image, so no picture was saved.";
                }
            }
        }
        else
        {
            imageNote = "Live video camera - no still frame to save.";
        }

        try
        {
            var parentId = await EnsureParentAsync(username, ct);
            var page = await wikiService.SavePageAsync(new WikiPageEditorModel
            {
                Title = Truncate($"{camera.Name} - {capturedAt.ToLocalTime():MMM d, yyyy h:mm tt}", 200),
                Icon = "📹",
                ParentWikiPageId = parentId,
                BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(
                    ReportMarkdown(camera, capturedAt, imageUrl, imageNote, analysis, username)))
            }, username, cancellationToken: ct);
            return new(true, page.Id, imageUrl is null ? "Report saved without an image." : "Report saved to Sentinel.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Overwatch report: saving the Sentinel page failed for {Camera}", camera.Id);
            return new(false, null, "The report couldn't be saved to Sentinel.");
        }
    }

    public static string ReportMarkdown(CameraFeed camera, DateTimeOffset capturedAt, string? imageUrl, string? imageNote, string? analysis, string username)
    {
        var lat = camera.Latitude.ToString("0.#####", CultureInfo.InvariantCulture);
        var lon = camera.Longitude.ToString("0.#####", CultureInfo.InvariantCulture);
        var md = new StringBuilder();
        if (imageUrl is not null) md.Append("![").Append(Escape(camera.Name)).Append("](").Append(imageUrl).AppendLine(")").AppendLine();
        else if (imageNote is not null) md.Append("_").Append(imageNote).AppendLine("_").AppendLine();
        md.Append("**Camera:** ").AppendLine(Escape(camera.Name)).AppendLine();
        md.Append("**Source:** [").Append(Escape(camera.SourceName)).Append("](").Append(camera.SourceAttributionUrl).AppendLine(")").AppendLine();
        md.Append("**Location:** ").Append(lat).Append(", ").Append(lon)
          .Append(" ([map](https://www.openstreetmap.org/?mlat=").Append(lat).Append("&mlon=").Append(lon).Append("#map=15/").Append(lat).Append('/').Append(lon).AppendLine("))").AppendLine();
        md.Append("**Captured:** ").Append(capturedAt.ToLocalTime().ToString("dddd, MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture))
          .Append(" (").Append(capturedAt.ToString("u", CultureInfo.InvariantCulture)).AppendLine(")").AppendLine();
        md.Append("**Saved by:** ").AppendLine(Escape(username)).AppendLine();
        md.AppendLine("## SentinelGPT notes").AppendLine();
        md.AppendLine(string.IsNullOrWhiteSpace(analysis) ? "_No analysis was run before saving. Use ANALYZE on the camera first to include one._" : analysis.Trim()).AppendLine();
        md.AppendLine("## Notes").AppendLine();
        md.AppendLine("_Add what you saw, follow-ups, and who you told._");
        return md.ToString();
    }

    private async Task<Guid> EnsureParentAsync(string username, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var parentId = await db.WikiPages.AsNoTracking()
            .Where(p => p.Title == ReportsParentTitle && p.ParentWikiPageId == null && p.TrashedAt == null)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (parentId is { } existing) return existing;
        var parent = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = ReportsParentTitle,
            Icon = "📹",
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(
                "Camera reports saved from Overwatch. Each page keeps the frame as it looked when it was saved."))
        }, username, cancellationToken: ct);
        return parent.Id;
    }

    private async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxSnapshotBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length is 0 or > (int)MaxSnapshotBytes ? null : bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Overwatch report: snapshot download failed for {Url}", url);
            return null;
        }
    }

    private static string Slug(string value) =>
        new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');

    // Camera names are plain text; keep markdown punctuation from turning into formatting.
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
