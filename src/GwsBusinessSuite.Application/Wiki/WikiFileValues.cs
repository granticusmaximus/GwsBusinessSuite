namespace GwsBusinessSuite.Application.Wiki;

// A Files property's value is a list of strings (same storage as MultiSelect). An uploaded file
// is stored as "sentinel-file:{id}:{file name}", where id is a SentinelImportedFile row served
// by /admin/sentinel/files/{id}. Anything else is an older free-text value (often a URL) and is
// shown as it is.
public sealed record WikiFileReference(Guid Id, string FileName)
{
    public string DownloadUrl => $"/admin/sentinel/files/{Id}";
    public bool IsImage => WikiFileValues.ContentTypeFor(FileName).StartsWith("image/", StringComparison.Ordinal);
}

public static class WikiFileValues
{
    public const long MaxBytes = 25 * 1024 * 1024;
    public const int MaxFilesPerUpload = 10;
    private const string Prefix = "sentinel-file:";

    public static string Format(Guid id, string fileName) => $"{Prefix}{id}:{fileName}";

    public static WikiFileReference? Parse(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = value[Prefix.Length..];
        var separator = rest.IndexOf(':');
        if (separator < 0 || !Guid.TryParse(rest[..separator], out var id)) return null;
        var name = rest[(separator + 1)..];
        return new WikiFileReference(id, string.IsNullOrWhiteSpace(name) ? "file" : name);
    }

    // Just the name, without any path a browser might send, and never empty.
    public static string CleanFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        return string.IsNullOrEmpty(name) ? "file" : name;
    }

    // Chosen by the server from the extension, never taken from the upload request. The download
    // endpoint only renders images (not SVG), PDFs, audio and video inline; everything else -
    // including anything unrecognised - downloads.
    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream"
    };
}
