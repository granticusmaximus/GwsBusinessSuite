using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

public sealed record TimelapseFrame(string FileName, DateTimeOffset CapturedAt);

// Time-lapse frames for favorited cameras, as plain JPEG files under one folder per camera
// (Overwatch:TimelapsePath, /app/data/overwatch-timelapse in production; not part of backups -
// the frames are disposable). Fed by CameraHealthBackgroundService's 10-minute favorites sweep,
// which only hands over a frame when the picture changed, so a frozen camera costs nothing.
// Capped two ways: frames per camera (a day at 10-minute intervals) and total bytes (oldest
// frames across all cameras go first).
public sealed partial class CameraTimelapseStore(string rootPath, ILogger<CameraTimelapseStore> logger)
{
    public const int MaxFramesPerCamera = 144;
    public const long MaxTotalBytes = 500L * 1024 * 1024;
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    [GeneratedRegex(@"^\d{8}T\d{6}Z\.jpg$")]
    private static partial Regex FrameNameRegex();

    // Camera ids come from many providers ("hk-td-H429F", "gdot-event-12"); folder names keep
    // only safe characters so an id can never point outside the root.
    public static string FolderFor(string cameraId) =>
        new(cameraId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    public async Task SaveFrameAsync(string cameraId, byte[] jpeg, DateTimeOffset capturedAt, CancellationToken ct = default)
    {
        try
        {
            var folder = Path.Combine(rootPath, FolderFor(cameraId));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, capturedAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".jpg");
            await File.WriteAllBytesAsync(path, jpeg, ct);

            var frames = Directory.GetFiles(folder, "*.jpg").Order(StringComparer.Ordinal).ToList();
            foreach (var old in frames.Take(Math.Max(0, frames.Count - MaxFramesPerCamera))) File.Delete(old);
            EnforceTotalCap();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Time-lapse: couldn't store a frame for {Camera} under {Root}", cameraId, rootPath);
        }
    }

    public IReadOnlyList<TimelapseFrame> ListFrames(string cameraId)
    {
        var folder = Path.Combine(rootPath, FolderFor(cameraId));
        if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, "*.jpg")
            .Select(Path.GetFileName)
            .Where(name => name is not null && FrameNameRegex().IsMatch(name))
            .Order(StringComparer.Ordinal)
            .Select(name => new TimelapseFrame(name!, DateTimeOffset.ParseExact(name![..^4], TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))
            .ToList();
    }

    // Null unless the name is exactly a frame file name - the endpoint passes user input here.
    public string? FramePath(string cameraId, string fileName)
    {
        if (!FrameNameRegex().IsMatch(fileName)) return null;
        var path = Path.Combine(rootPath, FolderFor(cameraId), fileName);
        return File.Exists(path) ? path : null;
    }

    private void EnforceTotalCap()
    {
        if (!Directory.Exists(rootPath)) return;
        var files = new DirectoryInfo(rootPath).GetFiles("*.jpg", SearchOption.AllDirectories)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= MaxTotalBytes) break;
            total -= file.Length;
            file.Delete();
        }
    }
}
