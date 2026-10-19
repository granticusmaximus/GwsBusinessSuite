using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class MediaStorageOptions
{
    public const string SectionName = "Media";
    // Inside the persisted data volume in production (docker-compose mounts /app/data).
    public string StoragePath { get; set; } = "/app/data/media";
}

// Media files in a folder on disk. Writes go to a temporary file first and are then moved into
// place, so a crash never leaves a half-written image under its real name.
public sealed class FileSystemMediaFileStore(IOptions<MediaStorageOptions> options) : IMediaFileStore
{
    private readonly string _root = Path.GetFullPath(options.Value.StoragePath);

    public async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var target = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var target = Resolve(path);
        try { return await File.ReadAllBytesAsync(target, cancellationToken); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    public long? GetLength(string path)
    {
        var info = new FileInfo(Resolve(path));
        return info.Exists ? info.Length : null;
    }

    public void Delete(string path)
    {
        var target = Resolve(path);
        if (File.Exists(target)) File.Delete(target);
    }

    public void DeleteStartingWith(string folder, string namePrefix)
    {
        var directory = Resolve(folder);
        if (!Directory.Exists(directory) || namePrefix.Length == 0) return;
        foreach (var file in Directory.EnumerateFiles(directory, namePrefix + "*")) File.Delete(file);
    }

    // Keeps every path inside the media folder.
    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != _root)
            throw new InvalidOperationException("Media path is outside the media folder.");
        return full;
    }
}
