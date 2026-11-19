using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Infrastructure.Services;

// Camera health history on disk (19-11-26): camera-health.json beside World News' database. Written
// to a temporary file and moved into place, so a crash mid-write never leaves half a file.
public sealed class CameraHealthStateFile(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Path { get; } = path;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CameraHealthService.Sample>>> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path)) return new Dictionary<string, IReadOnlyList<CameraHealthService.Sample>>();
        await using var stream = File.OpenRead(Path);
        var saved = await JsonSerializer.DeserializeAsync<Dictionary<string, List<CameraHealthService.Sample>>>(stream, Json, ct);
        return saved?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<CameraHealthService.Sample>)pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, IReadOnlyList<CameraHealthService.Sample>>();
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, IReadOnlyList<CameraHealthService.Sample>> snapshot, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, snapshot, Json, ct);
        File.Move(temporary, Path, overwrite: true);
    }
}
