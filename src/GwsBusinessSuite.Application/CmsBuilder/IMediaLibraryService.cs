namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IMediaLibraryService
{
    Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an uploaded image. The content type is determined from the file's own bytes,
    /// not from any client-supplied value, so callers don't pass one.
    /// </summary>
    Task<MediaAssetSummary> UploadAsync(
        string fileName,
        byte[] content,
        string altText,
        CancellationToken cancellationToken = default);

    Task<(string ContentType, byte[] Content)?> GetContentAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);

    // Falls back to the full-size content (via the same DataUri) when the asset has no
    // stored thumbnail - either it was uploaded before thumbnailing existed, the original
    // was already small enough that one wasn't generated, or generation failed at upload
    // time. Callers never need to branch on which case it is.
    Task<(string ContentType, byte[] Content)?> GetThumbnailContentAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);

    Task<MediaAssetSummary?> UpdateAltTextAsync(Guid mediaAssetId, string altText, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);
}
