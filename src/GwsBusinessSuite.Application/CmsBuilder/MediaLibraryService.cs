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
        string contentType,
        byte[] content,
        string altText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.", nameof(fileName));
        }

        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only image uploads are supported.", nameof(contentType));
        }

        if (content.Length == 0)
        {
            throw new ArgumentException("Uploaded file is empty.", nameof(content));
        }

        if (content.Length > MaxContentBytes)
        {
            throw new ArgumentException($"File exceeds the {MaxContentBytes / 1024 / 1024} MB upload limit.", nameof(content));
        }

        var asset = new MediaAsset
        {
            FileName = fileName.Trim(),
            ContentType = contentType.Trim(),
            DataUri = $"data:{contentType.Trim()};base64,{Convert.ToBase64String(content)}",
            AltText = altText?.Trim() ?? string.Empty,
            SizeBytes = content.Length,
            CreatedBy = "cms-media-library"
        };

        await dbContext.MediaAssets.AddAsync(asset, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSummary(asset);
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
