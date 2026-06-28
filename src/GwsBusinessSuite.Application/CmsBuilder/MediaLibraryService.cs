using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class MediaLibraryService(IAppDbContext dbContext) : IMediaLibraryService
{
    // Matches the unbounded-TEXT base64-in-DB pattern already used for article hero images
    // (see ArticleMarkdownRenderer/SeoArticleDraft), but media assets are uploaded by hand
    // rather than AI-generated, so a hard cap guards against someone dropping a multi-MB
    // file into a SQLite TEXT column.
    private const long MaxContentBytes = 8 * 1024 * 1024;

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
        var assets = await dbContext.MediaAssets
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return assets
            .OrderByDescending(asset => asset.CreatedAt)
            .Select(ToSummary)
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

        if (content.Length > MaxContentBytes)
        {
            throw new ArgumentException($"File exceeds the {MaxContentBytes / 1024 / 1024} MB upload limit.", nameof(content));
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
            AltText = altText?.Trim() ?? string.Empty,
            SizeBytes = content.Length,
            CreatedBy = "cms-media-library"
        };

        await dbContext.MediaAssets.AddAsync(asset, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSummary(asset);
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
        Url = $"/media/{asset.Id}"
    };
}
