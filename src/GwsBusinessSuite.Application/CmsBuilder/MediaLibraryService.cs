using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class MediaLibraryService(IAppDbContext dbContext, ISiteSettingsService siteSettingsService) : IMediaLibraryService
{
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

        var asset = new MediaAsset
        {
            FileName = fileName.Trim(),
            ContentType = detectedContentType,
            DataUri = $"data:{detectedContentType};base64,{Convert.ToBase64String(content)}",
            ThumbnailDataUri = TryGenerateThumbnail(content),
            AltText = altText?.Trim() ?? string.Empty,
            SizeBytes = content.Length,
            CreatedBy = "cms-media-library"
        };

        await dbContext.MediaAssets.AddAsync(asset, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSummary(asset);
    }

    // Best-effort: a thumbnail failure (corrupt/unsupported-by-SkiaSharp bytes, e.g. an
    // animated GIF's exotic encoding) must never block the upload itself, since the full
    // asset is already validated and stored regardless. Returns null (meaning "just serve
    // the full asset for this one too") both on failure and when the original is already
    // small enough that a separate smaller copy wouldn't help.
    private static string? TryGenerateThumbnail(byte[] content)
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
            return $"data:image/jpeg;base64,{Convert.ToBase64String(encoded.ToArray())}";
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
        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == mediaAssetId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var commaIndex = asset.DataUri.IndexOf(',');
        if (commaIndex < 0)
        {
            return null;
        }

        var base64Payload = asset.DataUri[(commaIndex + 1)..];
        return (asset.ContentType, Convert.FromBase64String(base64Payload));
    }

    public async Task<(string ContentType, byte[] Content)?> GetThumbnailContentAsync(Guid mediaAssetId, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .Select(a => new { a.Id, a.ContentType, a.DataUri, a.ThumbnailDataUri })
            .FirstOrDefaultAsync(a => a.Id == mediaAssetId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var (effectiveDataUri, effectiveContentType) = asset.ThumbnailDataUri is null
            ? (asset.DataUri, asset.ContentType)
            : (asset.ThumbnailDataUri, "image/jpeg");

        var commaIndex = effectiveDataUri.IndexOf(',');
        if (commaIndex < 0)
        {
            return null;
        }

        var base64Payload = effectiveDataUri[(commaIndex + 1)..];
        return (effectiveContentType, Convert.FromBase64String(base64Payload));
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
