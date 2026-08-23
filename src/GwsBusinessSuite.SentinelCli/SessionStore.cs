using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.SentinelCli;

public sealed record PersistedSession(
    string WorkspaceRoot, string Model, DateTimeOffset UpdatedAt, IReadOnlyList<OllamaChatMessage> Messages);

// Sessions are scoped to "which workspace was this" the same way -C/--repo already scopes
// everything else in this tool - one JSON file per session under sessionsDirectory, filename
// carries both the workspace slug and a sortable creation timestamp so listing needs no file
// reads. No locking, no retention/pruning: a single-user local tool doesn't need either; the
// displayed list is simply capped.
public sealed class SessionStore(string sessionsDirectory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task<string> SaveAsync(
        string? existingPath, string workspaceRoot, string model,
        IReadOnlyList<OllamaChatMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sessionsDirectory);
        var path = existingPath ?? Path.Combine(
            sessionsDirectory, $"{ComputeWorkspaceSlug(workspaceRoot)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
        var session = new PersistedSession(workspaceRoot, model, DateTimeOffset.UtcNow, messages);
        await WorkspaceTools.WriteAtomicallyAsync(path, JsonSerializer.Serialize(session, SerializerOptions), cancellationToken);
        return path;
    }

    public IReadOnlyList<(string Path, PersistedSession Session)> ListForWorkspace(string workspaceRoot)
    {
        if (!Directory.Exists(sessionsDirectory)) return [];

        var prefix = ComputeWorkspaceSlug(workspaceRoot) + "-";
        var results = new List<(string Path, PersistedSession Session)>();
        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, prefix + "*.json"))
        {
            try
            {
                var session = JsonSerializer.Deserialize<PersistedSession>(File.ReadAllText(file), SerializerOptions);
                if (session is not null) results.Add((file, session));
            }
            catch (JsonException)
            {
                // A partial/corrupted session file shouldn't hide every other saved session.
            }
        }
        return results.OrderByDescending(item => item.Session.UpdatedAt).Take(20).ToArray();
    }

    public async Task<PersistedSession?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PersistedSession>(
            await File.ReadAllTextAsync(path, cancellationToken), SerializerOptions);
    }

    public static string ComputeWorkspaceSlug(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
        var safeName = string.IsNullOrEmpty(name)
            ? "root"
            : new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];
        return $"{safeName}-{hash}";
    }
}
