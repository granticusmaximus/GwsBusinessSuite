using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GwsBusinessSuite.Infrastructure.Services;

// Export a Sentinel page (and optionally its sub-pages) as PDF or Word (Sentinel item 20).
// Sub-pages the user can't view are left out, with everything under them. Images are embedded
// only when they're this app's own (the media library's /media/{id}, or files imported into
// Sentinel at /admin/sentinel/files/{id}) - read straight from the database, never fetched over
// the network, so a page can't make the server request an arbitrary address. Anything else
// prints as a link.
public sealed partial class SentinelPageExportService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<SentinelPageExportService> logger,
    IMediaLibraryService? mediaLibrary = null,
    ISentinelAccessService? accessService = null) : ISentinelPageExportService
{
    public const int MaxPages = 50;
    public const int MaxDepth = 6;
    public const int MaxImages = 80;
    private const int MaxImageBytes = 15 * 1024 * 1024;

    public async Task<SentinelExportFile?> ExportAsync(Guid pageId, string username, SentinelExportFormat format, bool includeSubPages, CancellationToken cancellationToken = default)
    {
        var document = await BuildAsync(pageId, username, includeSubPages, cancellationToken);
        if (document is null) return null;
        var root = await dbContext.WikiPages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.Slug).FirstOrDefaultAsync(cancellationToken);
        var name = SafeFileName(string.IsNullOrWhiteSpace(root) ? document.Title : root);
        return format == SentinelExportFormat.Pdf
            ? new SentinelExportFile(SentinelExportWriters.RenderPdf(document), "application/pdf", $"{name}.pdf")
            : new SentinelExportFile(SentinelExportWriters.RenderDocx(document),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{name}.docx");
    }

    public async Task<SentinelExportDocument?> BuildAsync(Guid pageId, string username, bool includeSubPages, CancellationToken cancellationToken = default)
    {
        var root = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId && p.TrashedAt == null, cancellationToken);
        if (root is null || !await CanViewAsync(pageId, username, cancellationToken)) return null;

        var order = new List<(Guid Id, int Depth)> { (root.Id, 0) };
        if (includeSubPages)
        {
            var tree = await dbContext.WikiPages.AsNoTracking()
                .Where(p => p.TrashedAt == null && p.ParentWikiPageId != null)
                .Select(p => new { p.Id, p.ParentWikiPageId, p.SortOrder, p.Title })
                .ToListAsync(cancellationToken);
            var children = tree.GroupBy(p => p.ParentWikiPageId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.SortOrder).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase).Select(p => p.Id).ToList());
            var visible = accessService is null
                ? null
                : await accessService.GetAccessibleTargetsAsync(
                    tree.Select(p => new SentinelAccessTarget(p.Id, IsDatabase: false)).ToList(), username, SentinelAccessLevels.View, cancellationToken);
            void Walk(Guid parent, int depth)
            {
                if (depth > MaxDepth || !children.TryGetValue(parent, out var kids)) return;
                foreach (var kid in kids)
                {
                    if (order.Count >= MaxPages) return;
                    if (visible is not null && !visible.Contains(new SentinelAccessTarget(kid, IsDatabase: false))) continue;
                    order.Add((kid, depth));
                    Walk(kid, depth + 1);
                }
            }
            Walk(root.Id, 1);
        }

        var ids = order.Select(o => o.Id).ToList();
        var rows = (await dbContext.WikiPages.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken)).ToDictionary(p => p.Id);
        var pages = order.Where(o => rows.ContainsKey(o.Id))
            .Select(o => new SentinelExportPage(o.Id, Title(rows[o.Id]), rows[o.Id].Icon, o.Depth, WikiBlockJson.ParseBlocks(rows[o.Id].BlocksJson)))
            .ToList();

        var imageUrls = pages.SelectMany(p => SentinelExportFlattener.Flatten(p.Blocks)).OfType<ExportImageRef>().Select(i => i.Url).Distinct().Take(MaxImages);
        var images = new Dictionary<string, SentinelExportImage>(StringComparer.Ordinal);
        foreach (var url in imageUrls)
        {
            if (await LoadImageAsync(url, cancellationToken) is { } image) images[url] = image;
        }
        return new SentinelExportDocument(Title(root), timeProvider.GetUtcNow(), username, pages, images);
    }

    private async Task<bool> CanViewAsync(Guid pageId, string username, CancellationToken cancellationToken) =>
        accessService is null || await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.View, cancellationToken);

    private async Task<SentinelExportImage?> LoadImageAsync(string url, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            if (MediaUrl().Match(url) is { Success: true } media && mediaLibrary is not null)
            {
                bytes = (await mediaLibrary.GetContentAsync(Guid.Parse(media.Groups[1].Value), cancellationToken))?.Content;
            }
            else if (SentinelFileUrl().Match(url) is { Success: true } file)
            {
                var id = Guid.Parse(file.Groups[1].Value);
                bytes = await dbContext.SentinelImportedFiles.AsNoTracking().Where(f => f.Id == id).Select(f => f.Content).FirstOrDefaultAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Export: couldn't read image {Url}", url);
        }
        return bytes is { Length: > 0 and <= MaxImageBytes } ? Normalize(bytes) : null;
    }

    // PNG and JPEG are kept as they are; anything else Skia can read (WebP, GIF, BMP...) becomes
    // PNG, so Word and PDF readers all open it. Returns null for bytes that aren't an image.
    public static SentinelExportImage? Normalize(byte[] bytes)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null) return null;
            var (width, height) = (codec.Info.Width, codec.Info.Height);
            if (width <= 0 || height <= 0) return null;
            if (codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg)
                return new SentinelExportImage(bytes, codec.EncodedFormat == SKEncodedImageFormat.Png ? "png" : "jpeg", width, height);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null) return null;
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data is null ? null : new SentinelExportImage(data.ToArray(), "png", bitmap.Width, bitmap.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Title(WikiPage page) => string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title.Trim();

    private static string SafeFileName(string value)
    {
        var cleaned = FileNameUnsafe().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return cleaned.Length == 0 ? "sentinel-page" : cleaned.Length > 80 ? cleaned[..80].TrimEnd('-') : cleaned;
    }

    [GeneratedRegex(@"^(?:https?://[^/]+)?/media/([0-9a-fA-F-]{36})(?:/thumb)?/?(?:[?#].*)?$")]
    private static partial Regex MediaUrl();

    [GeneratedRegex(@"^(?:https?://[^/]+)?/admin/sentinel/files/([0-9a-fA-F-]{36})/?(?:[?#].*)?$")]
    private static partial Regex SentinelFileUrl();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex FileNameUnsafe();
}
