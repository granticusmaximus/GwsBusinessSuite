using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Images live on disk (IMediaFileStore) once a file store is available, with the database copy
// kept for a grace period after the move (see MediaAsset.StoredAt) and then cleared. Without a
// file store (some tests) everything stays in the database, as it always did.
public sealed class MediaLibraryService(
    IAppDbContext dbContext,
    ISiteSettingsService siteSettingsService,
    IMediaFileStore? fileStore = null,
    TimeProvider? timeProvider = null) : IMediaLibraryService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Default matches the unbounded-TEXT base64-in-DB pattern already used for article hero
    // images (see ArticleMarkdownRenderer/SeoArticleDraft), but media assets are uploaded by
    // hand rather than AI-generated, so a hard cap guards against someone dropping a
    // multi-MB file into a SQLite TEXT column. Configurable via Settings > Media.
    private const long DefaultMaxContentBytes = 8 * 1024 * 1024;

    // Longest edge for a generated thumbnail - plenty for the admin grid's small tiles.
    private const int ThumbnailMaxDimension = 320;

    // Raster-format magic bytes. A browser's reported Content-Type is attacker-controlled
    // (trivial to relabel an .svg or .html payload as "image/png" before upload), and that
    // same value would later be echoed back as the response Content-Type by the /media/{id}
    // endpoint — so the stored content type is derived from the file's actual bytes, never
    // from client input. SVG is deliberately not in this list: it's XML and can carry
    // <script>, so it isn't treated as a safe image format here.
    private static readonly (byte[] Signature, string ContentType)[] ImageSignatures =
    [
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png"),
        ([0xFF, 0xD8, 0xFF], "image/jpeg"),
        ([0x47, 0x49, 0x46, 0x38, 0x37, 0x61], "image/gif"),
        ([0x47, 0x49, 0x46, 0x38, 0x39, 0x61], "image/gif")
    ];

    public async Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Explicitly projects away DataUri - without this, EF Core materializes every
        // asset's full base64 file content (up to the configured upload cap, default
        // 8MB/asset) just to build a lightweight summary list that never uses it. This
        // runs on every /admin/media page load and after every upload/delete/alt-text save.
        var assets = await dbContext.MediaAssets
            .AsNoTracking()
            .Select(asset => new
            {
                asset.Id,
                asset.FileName,
                asset.ContentType,
                asset.AltText,
                asset.SizeBytes,
                asset.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return assets
            .OrderByDescending(asset => asset.CreatedAt)
            .Select(asset => new MediaAssetSummary
            {
                Id = asset.Id,
                FileName = asset.FileName,
                ContentType = asset.ContentType,
                AltText = asset.AltText,
                SizeBytes = asset.SizeBytes,
                CreatedAt = asset.CreatedAt,
                Url = $"/media/{asset.Id}",
                ThumbnailUrl = $"/media/{asset.Id}/thumb"
            })
            .ToList();
    }

    public async Task<MediaAssetSummary> UploadAsync(
        string fileName,
        byte[] content,
        string altText,
        CancellationToken cancellationToken = default)
    {
        var (normalizedFileName, detectedContentType) = await ValidateUploadAsync(fileName, content, cancellationToken);
        var thumbnail = TryGenerateThumbnail(content);

        var asset = new MediaAsset
        {
            FileName = normalizedFileName,
            ContentType = detectedContentType,
            // Kept alongside the file during the grace period - see the class comment.
            DataUri = ToDataUri(detectedContentType, content),
            ThumbnailDataUri = thumbnail is null ? null : ToDataUri("image/jpeg", thumbnail),
            AltText = altText?.Trim() ?? string.Empty,
            SizeBytes = content.Length,
            CreatedAt = _clock.GetUtcNow(),
            CreatedBy = "cms-media-library"
        };
        await StoreFilesAsync(asset, content, thumbnail, cancellationToken);

        await dbContext.MediaAssets.AddAsync(asset, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSummary(asset);
    }

    public async Task<MediaAssetSummary?> ReplaceAsync(
        Guid mediaAssetId,
        string fileName,
        byte[] content,
        string altText,
        CancellationToken cancellationToken = default)
    {
        var (normalizedFileName, detectedContentType) = await ValidateUploadAsync(fileName, content, cancellationToken);
        var asset = await dbContext.MediaAssets
            .FirstOrDefaultAsync(item => item.Id == mediaAssetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var thumbnail = TryGenerateThumbnail(content);
        var previousPath = asset.StoragePath;
        asset.FileName = normalizedFileName;
        asset.ContentType = detectedContentType;
        asset.DataUri = ToDataUri(detectedContentType, content);
        asset.ThumbnailDataUri = thumbnail is null ? null : ToDataUri("image/jpeg", thumbnail);
        asset.AltText = altText?.Trim() ?? string.Empty;
        asset.SizeBytes = content.Length;
        asset.UpdatedBy = "cms-media-library";
        asset.UpdatedAt = _clock.GetUtcNow();
        if (fileStore is not null)
        {
            // The resized copies were of the old picture.
            fileStore.DeleteStartingWith(MediaFilePaths.VariantFolder, MediaFilePaths.VariantPrefix(asset.Id));
            await StoreFilesAsync(asset, content, thumbnail, cancellationToken);
            if (previousPath is not null && previousPath != asset.StoragePath) fileStore.Delete(previousPath);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(asset);
    }

    private async Task<(string FileName, string ContentType)> ValidateUploadAsync(
        string fileName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.", nameof(fileName));
        }

        if (content.Length == 0)
        {
            throw new ArgumentException("Uploaded file is empty.", nameof(content));
        }

        var settings = await siteSettingsService.GetSettingsAsync(cancellationToken);
        var maxContentBytes = settings.MaxMediaUploadSizeMb > 0
            ? settings.MaxMediaUploadSizeMb * 1024L * 1024L
            : DefaultMaxContentBytes;

        if (content.Length > maxContentBytes)
        {
            throw new ArgumentException($"File exceeds the {maxContentBytes / 1024 / 1024} MB upload limit.", nameof(content));
        }

        var detectedContentType = DetectImageContentType(content);
        if (detectedContentType is null)
        {
            throw new ArgumentException(
                "File does not appear to be a supported image (PNG, JPEG, GIF, or WEBP).",
                nameof(content));
        }

        return (fileName.Trim(), detectedContentType);
    }

    // Best-effort: a thumbnail failure (corrupt/unsupported-by-SkiaSharp bytes, e.g. an
    // animated GIF's exotic encoding) must never block the upload itself, since the full
    // asset is already validated and stored regardless. Returns null (meaning "just serve
    // the full asset for this one too") both on failure and when the original is already
    // small enough that a separate smaller copy wouldn't help.
    private static byte[]? TryGenerateThumbnail(byte[] content)
    {
        try
        {
            using var original = SKBitmap.Decode(content);
            if (original is null || (original.Width <= ThumbnailMaxDimension && original.Height <= ThumbnailMaxDimension))
            {
                return null;
            }

            var scale = Math.Min(
                (float)ThumbnailMaxDimension / original.Width,
                (float)ThumbnailMaxDimension / original.Height);
            var targetWidth = Math.Max(1, (int)Math.Round(original.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(original.Height * scale));

            using var resized = original.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
            if (resized is null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 80);
            return encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string? DetectImageContentType(byte[] content)
    {
        foreach (var (signature, imageContentType) in ImageSignatures)
        {
            if (content.Length >= signature.Length && content.AsSpan(0, signature.Length).SequenceEqual(signature))
            {
                return imageContentType;
            }
        }

        var isRiffContainer = content.Length >= 12
            && content[0] == (byte)'R' && content[1] == (byte)'I' && content[2] == (byte)'F' && content[3] == (byte)'F'
            && content[8] == (byte)'W' && content[9] == (byte)'E' && content[10] == (byte)'B' && content[11] == (byte)'P';

        return isRiffContainer ? "image/webp" : null;
    }

    public async Task<(string ContentType, byte[] Content)?> GetContentAsync(Guid mediaAssetId, CancellationToken cancellationToken = default)
    {
        var original = await ReadOriginalAsync(mediaAssetId, cancellationToken);
        return original is null ? null : (original.Value.ContentType, original.Value.Content);
    }

    public async Task<(string ContentType, byte[] Content)?> GetThumbnailContentAsync(Guid mediaAssetId, CancellationToken cancellationToken = default)
    {
        if (fileStore is not null && await fileStore.ReadAsync(MediaFilePaths.Thumbnail(mediaAssetId), cancellationToken) is { } file)
            return ("image/jpeg", file);
        var asset = await dbContext.MediaAssets.AsNoTracking()
            .Where(item => item.Id == mediaAssetId)
            .Select(item => new { item.ThumbnailDataUri })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null) return null;
        // No thumbnail (a small original, or generation failed): the original serves.
        return asset.ThumbnailDataUri is { } thumbnail && FromDataUri(thumbnail) is { } bytes
            ? ("image/jpeg", bytes)
            : await GetContentAsync(mediaAssetId, cancellationToken);
    }

    public async Task<MediaImage?> GetImageAsync(Guid mediaAssetId, int? width, bool acceptsWebp, CancellationToken cancellationToken = default)
    {
        var meta = await dbContext.MediaAssets.AsNoTracking()
            .Where(item => item.Id == mediaAssetId)
            .Select(item => new { item.ContentType, item.CreatedAt, item.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (meta is null) return null;
        var modified = meta.UpdatedAt ?? meta.CreatedAt;
        var version = modified.UtcTicks;
        var target = MediaImageSizes.Snap(width);
        // GIFs keep their animation, so they're always served as they are.
        var webp = acceptsWebp && meta.ContentType is "image/jpeg" or "image/png";
        if (meta.ContentType == "image/gif") { target = null; webp = false; }
        var extension = webp ? ".webp" : MediaFilePaths.Extension(meta.ContentType);
        var etag = $"\"{mediaAssetId:N}-{version}-{target?.ToString() ?? "o"}{extension}\"";

        if (target is null && !webp)
        {
            var original = await ReadOriginalAsync(mediaAssetId, cancellationToken);
            return original is null ? null : new MediaImage(original.Value.ContentType, original.Value.Content, etag, modified);
        }

        var variantPath = MediaFilePaths.Variant(mediaAssetId, version, target ?? 0, extension);
        if (fileStore is not null && await fileStore.ReadAsync(variantPath, cancellationToken) is { } cached)
            return new MediaImage(webp ? "image/webp" : meta.ContentType, cached, etag, modified);

        var source = await ReadOriginalAsync(mediaAssetId, cancellationToken);
        if (source is null) return null;
        var produced = TryEncode(source.Value.Content, target, webp ? SKEncodedImageFormat.Webp : FormatOf(meta.ContentType));
        // Can't decode it (or nothing would change): the original serves.
        if (produced is null) return new MediaImage(source.Value.ContentType, source.Value.Content, etag, modified);
        if (fileStore is not null) await fileStore.WriteAsync(variantPath, produced, cancellationToken);
        return new MediaImage(webp ? "image/webp" : meta.ContentType, produced, etag, modified);
    }

    public async Task<int> MoveToFilesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (fileStore is null) return 0;
        var pending = await dbContext.MediaAssets
            .Where(item => item.StoragePath == null && item.DataUri != "")
            .OrderBy(item => item.Id)
            .Take(Math.Clamp(batchSize, 1, 200))
            .ToListAsync(cancellationToken);
        var moved = 0;
        foreach (var asset in pending)
        {
            if (FromDataUri(asset.DataUri) is not { } content) continue;
            var thumbnail = asset.ThumbnailDataUri is { } stored ? FromDataUri(stored) : TryGenerateThumbnail(content);
            await StoreFilesAsync(asset, content, thumbnail, cancellationToken);
            moved++;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return moved;
    }

    public async Task<int> ReleaseDatabaseCopiesAsync(TimeSpan gracePeriod, int batchSize, CancellationToken cancellationToken = default)
    {
        if (fileStore is null) return 0;
        var cutoff = _clock.GetUtcNow() - gracePeriod;
        // SQLite/EF Core can't translate DateTimeOffset comparisons - filter dates client-side.
        var candidates = (await dbContext.MediaAssets
                .Where(item => item.StoragePath != null && item.DataUri != "")
                .ToListAsync(cancellationToken))
            .Where(item => item.StoredAt is { } stored && stored <= cutoff)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToList();
        var released = 0;
        foreach (var asset in candidates)
        {
            // Only once the file is really there, whole.
            if (fileStore.GetLength(asset.StoragePath!) != asset.SizeBytes) continue;
            asset.DataUri = string.Empty;
            asset.ThumbnailDataUri = null;
            released++;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return released;
    }

    // The original's bytes: from its file, or from the database copy (not yet moved, or the file
    // went missing - the database copy is still there during the grace period).
    private async Task<(string ContentType, byte[] Content)?> ReadOriginalAsync(Guid mediaAssetId, CancellationToken cancellationToken)
    {
        var meta = await dbContext.MediaAssets.AsNoTracking()
            .Where(item => item.Id == mediaAssetId)
            .Select(item => new { item.ContentType, item.StoragePath })
            .FirstOrDefaultAsync(cancellationToken);
        if (meta is null) return null;
        if (fileStore is not null && meta.StoragePath is { } path && await fileStore.ReadAsync(path, cancellationToken) is { } file)
            return (meta.ContentType, file);
        var dataUri = await dbContext.MediaAssets.AsNoTracking()
            .Where(item => item.Id == mediaAssetId)
            .Select(item => item.DataUri)
            .FirstOrDefaultAsync(cancellationToken);
        return dataUri is not null && FromDataUri(dataUri) is { } bytes ? (meta.ContentType, bytes) : null;
    }

    private async Task StoreFilesAsync(MediaAsset asset, byte[] content, byte[]? thumbnail, CancellationToken cancellationToken)
    {
        if (fileStore is null) return;
        var path = MediaFilePaths.Original(asset.Id, asset.ContentType);
        await fileStore.WriteAsync(path, content, cancellationToken);
        if (thumbnail is not null) await fileStore.WriteAsync(MediaFilePaths.Thumbnail(asset.Id), thumbnail, cancellationToken);
        else fileStore.Delete(MediaFilePaths.Thumbnail(asset.Id));
        asset.StoragePath = path;
        asset.StoredAt = _clock.GetUtcNow();
        (asset.Width, asset.Height) = MeasureOrNull(content);
    }

    private static (int?, int?) MeasureOrNull(byte[] content)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(content));
            return codec is null ? (null, null) : (codec.Info.Width, codec.Info.Height);
        }
        catch { return (null, null); }
    }

    // A resized (never enlarged) and/or re-encoded copy; null when it can't be made or wouldn't change anything.
    private static byte[]? TryEncode(byte[] content, int? width, SKEncodedImageFormat format)
    {
        try
        {
            using var original = SKBitmap.Decode(content);
            if (original is null) return null;
            var resize = width is { } wanted && original.Width > wanted;
            if (!resize && format != SKEncodedImageFormat.Webp) return null;
            using var bitmap = resize
                ? original.Resize(new SKImageInfo(width!.Value, Math.Max(1, (int)Math.Round(original.Height * (double)width.Value / original.Width))), SKSamplingOptions.Default)
                : original.Copy();
            if (bitmap is null) return null;
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(format, format == SKEncodedImageFormat.Png ? 100 : 82);
            return encoded?.ToArray();
        }
        catch { return null; }
    }

    private static SKEncodedImageFormat FormatOf(string contentType) => contentType switch
    {
        "image/png" => SKEncodedImageFormat.Png,
        "image/webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Jpeg
    };

    private static string ToDataUri(string contentType, byte[] content) => $"data:{contentType};base64,{Convert.ToBase64String(content)}";

    private static byte[]? FromDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        if (comma < 0) return null;
        try { return Convert.FromBase64String(dataUri[(comma + 1)..]); }
        catch (FormatException) { return null; }
    }

    public async Task<MediaAssetSummary?> UpdateAltTextAsync(Guid mediaAssetId, string altText, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.MediaAssets.FirstOrDefaultAsync(item => item.Id == mediaAssetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        asset.AltText = altText?.Trim() ?? string.Empty;
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSummary(asset);
    }

    public async Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.MediaAssets.FirstOrDefaultAsync(item => item.Id == mediaAssetId, cancellationToken);
        if (asset is null)
        {
            return;
        }

        dbContext.MediaAssets.Remove(asset);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (fileStore is not null)
        {
            if (asset.StoragePath is { } path) fileStore.Delete(path);
            fileStore.Delete(MediaFilePaths.Thumbnail(asset.Id));
            fileStore.DeleteStartingWith(MediaFilePaths.VariantFolder, MediaFilePaths.VariantPrefix(asset.Id));
        }
    }

    private static MediaAssetSummary ToSummary(MediaAsset asset) => new()
    {
        Id = asset.Id,
        FileName = asset.FileName,
        ContentType = asset.ContentType,
        AltText = asset.AltText,
        SizeBytes = asset.SizeBytes,
        CreatedAt = asset.CreatedAt,
        Url = $"/media/{asset.Id}",
        ThumbnailUrl = $"/media/{asset.Id}/thumb"
    };
}
