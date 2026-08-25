using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.OllamaKit;

public sealed record PersistedConversation(
    string Model, DateTimeOffset UpdatedAt, IReadOnlyList<OllamaChatMessage> Messages, string? WorkspaceRoot = null);

// One JSON file per conversation under sessionsDirectory, filename is a sortable timestamp so
// listing needs no file reads for ordering. Ordinary chats have no "workspace" concept - a
// native chat tab just has "all my local conversations" - but Developer Mode conversations are
// scoped to whichever folder was open (WorkspaceRoot set, filename prefixed with its slug, same
// scheme SentinelCLI's own SessionStore uses for -C/--repo scoping) so List() and
// ListForWorkspace() never mix the two. No locking, no retention/pruning - a single local user
// doesn't need either; the displayed list is simply capped.
public sealed class ConversationSessionStore(string sessionsDirectory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task<string> SaveAsync(
        string? existingPath, string model, IReadOnlyList<OllamaChatMessage> messages, CancellationToken cancellationToken,
        string? workspaceRoot = null)
    {
        Directory.CreateDirectory(sessionsDirectory);
        var normalizedWorkspaceRoot = workspaceRoot is null ? null : Path.GetFullPath(workspaceRoot);
        var path = existingPath ?? Path.Combine(
            sessionsDirectory,
            normalizedWorkspaceRoot is null
                ? $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json"
                : $"{ComputeWorkspaceSlug(normalizedWorkspaceRoot)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
        var conversation = new PersistedConversation(model, DateTimeOffset.UtcNow, messages, normalizedWorkspaceRoot);
        await WriteAtomicallyAsync(path, JsonSerializer.Serialize(conversation, SerializerOptions), cancellationToken);
        return path;
    }

    public IReadOnlyList<(string Path, PersistedConversation Conversation)> List(int take = 50)
    {
        if (!Directory.Exists(sessionsDirectory)) return [];

        var results = new List<(string Path, PersistedConversation Conversation)>();
        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
        {
            try
            {
                var conversation = JsonSerializer.Deserialize<PersistedConversation>(File.ReadAllText(file), SerializerOptions);
                if (conversation is { WorkspaceRoot: null }) results.Add((file, conversation));
            }
            catch (JsonException)
            {
                // A partial/corrupted session file shouldn't hide every other saved session.
            }
        }
        return results.OrderByDescending(item => item.Conversation.UpdatedAt).Take(take).ToArray();
    }

    public IReadOnlyList<(string Path, PersistedConversation Conversation)> ListForWorkspace(string workspaceRoot, int take = 20)
    {
        if (!Directory.Exists(sessionsDirectory)) return [];

        var prefix = ComputeWorkspaceSlug(Path.GetFullPath(workspaceRoot)) + "-";
        var results = new List<(string Path, PersistedConversation Conversation)>();
        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, prefix + "*.json"))
        {
            try
            {
                var conversation = JsonSerializer.Deserialize<PersistedConversation>(File.ReadAllText(file), SerializerOptions);
                if (conversation is not null) results.Add((file, conversation));
            }
            catch (JsonException)
            {
                // A partial/corrupted session file shouldn't hide every other saved session.
            }
        }
        return results.OrderByDescending(item => item.Conversation.UpdatedAt).Take(take).ToArray();
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

    public async Task<PersistedConversation?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PersistedConversation>(
            await File.ReadAllTextAsync(path, cancellationToken), SerializerOptions);
    }

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
