namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IMediaLibraryService
{
    Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<MediaAssetSummary> UploadAsync(
        string fileName,
        string contentType,
        byte[] content,
        string altText,
        CancellationToken cancellationToken = default);

    Task<(string ContentType, byte[] Content)?> GetContentAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);
}
