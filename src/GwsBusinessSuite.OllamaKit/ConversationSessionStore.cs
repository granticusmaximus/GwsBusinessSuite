using System.Text.Json;

namespace GwsBusinessSuite.OllamaKit;

public sealed record PersistedConversation(string Model, DateTimeOffset UpdatedAt, IReadOnlyList<OllamaChatMessage> Messages);

// One JSON file per conversation under sessionsDirectory, filename is a sortable timestamp so
// listing needs no file reads for ordering. Unlike SentinelCli's SessionStore, there's no
// "workspace" concept to scope by - a native chat tab just has "all my local conversations".
// No locking, no retention/pruning - a single local user doesn't need either; the displayed
// list is simply capped.
public sealed class ConversationSessionStore(string sessionsDirectory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task<string> SaveAsync(
        string? existingPath, string model, IReadOnlyList<OllamaChatMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sessionsDirectory);
        var path = existingPath ?? Path.Combine(sessionsDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
        var conversation = new PersistedConversation(model, DateTimeOffset.UtcNow, messages);
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
                if (conversation is not null) results.Add((file, conversation));
            }
            catch (JsonException)
            {
                // A partial/corrupted session file shouldn't hide every other saved session.
            }
        }
        return results.OrderByDescending(item => item.Conversation.UpdatedAt).Take(take).ToArray();
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
