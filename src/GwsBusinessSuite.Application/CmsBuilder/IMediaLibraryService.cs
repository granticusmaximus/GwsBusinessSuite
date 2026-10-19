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

    /// <summary>
    /// Replaces an uploaded image in place so existing /media/{id} references remain valid.
    /// Content validation and upload-size limits are identical to a new upload.
    /// </summary>
    Task<MediaAssetSummary?> ReplaceAsync(
        Guid mediaAssetId,
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

    // What /media/{id} serves: the original, or with width a smaller copy (one of
    // MediaImageSizes.Widths, never larger than the original), as WebP when the browser accepts it
    // (not for GIFs, which would lose their animation). Copies are cached on disk.
    Task<MediaImage?> GetImageAsync(Guid mediaAssetId, int? width, bool acceptsWebp, CancellationToken cancellationToken = default);

    // Moves media still held only in the database onto disk (keeping the database copy for now).
    Task<int> MoveToFilesAsync(int batchSize, CancellationToken cancellationToken = default);

    // Clears database copies of media whose file has been in place longer than the grace period
    // and checks out (exists, right size). Returns how many were cleared.
    Task<int> ReleaseDatabaseCopiesAsync(TimeSpan gracePeriod, int batchSize, CancellationToken cancellationToken = default);
}

// One image response: its bytes plus the validator browsers revalidate with.
public sealed record MediaImage(string ContentType, byte[] Content, string ETag, DateTimeOffset LastModified);

public static class MediaImageSizes
{
    // The widths /media/{id}?w= serves (a request snaps up to the next one), and the srcset.
    public static readonly IReadOnlyList<int> Widths = [320, 640, 960, 1280, 1920];

    public static int? Snap(int? requested) =>
        requested is not > 0 ? null : Widths.FirstOrDefault(width => width >= requested, Widths[^1]);
}
