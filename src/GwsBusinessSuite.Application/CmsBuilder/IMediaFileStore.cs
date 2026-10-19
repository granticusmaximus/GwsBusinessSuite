namespace GwsBusinessSuite.Application.CmsBuilder;

// Where media files live (a folder in the persisted data volume - see FileSystemMediaFileStore).
// Paths are relative to that folder and always forward-slashed.
public interface IMediaFileStore
{
    Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default);
    // Null when the file isn't there.
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);
    long? GetLength(string path);
    void Delete(string path);
    // Removes every file in a folder whose name starts with the prefix (cached sizes of one image).
    void DeleteStartingWith(string folder, string namePrefix);
}

// The media folder's layout.
public static class MediaFilePaths
{
    // originals/ab/ab12…ef.png - sharded by the first two hex digits so no folder grows huge.
    public static string Original(Guid id, string contentType) => $"originals/{id.ToString("N")[..2]}/{id:N}{Extension(contentType)}";
    public static string Thumbnail(Guid id) => $"thumbs/{id:N}.jpg";
    public const string VariantFolder = "variants";
    public static string VariantPrefix(Guid id) => $"{id:N}-";
    public static string Variant(Guid id, long version, int width, string extension) => $"{VariantFolder}/{id:N}-{version}-w{width}{extension}";

    public static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".bin"
    };
}
