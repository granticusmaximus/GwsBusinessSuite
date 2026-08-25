using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.Application.MindMaps;

public sealed record MindMapNode(
    Guid Id,
    string Topic,
    IReadOnlyList<MindMapNode> Children)
{
    public static MindMapNode CreateRoot(string topic) => new(Guid.NewGuid(), topic, []);
}

public sealed record MindMapSummary(Guid Id, string Title, DateTimeOffset LastEditedAt);

public sealed record MindMapDetail(Guid Id, string Title, MindMapNode Root);

public static class MindMapTreeJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static MindMapNode ParseRoot(string treeJson)
    {
        if (string.IsNullOrWhiteSpace(treeJson))
        {
            return MindMapNode.CreateRoot("Untitled");
        }

        try { return JsonSerializer.Deserialize<MindMapNode>(treeJson.Trim(), Options) ?? MindMapNode.CreateRoot("Untitled"); }
        catch (JsonException) { return MindMapNode.CreateRoot("Untitled"); }
    }

    public static string Serialize(MindMapNode root) => JsonSerializer.Serialize(root, Options);

    public static string SerializeNewRoot(string topic) => Serialize(MindMapNode.CreateRoot(topic));
}

public interface IMindMapService
{
    Task<IReadOnlyList<MindMapSummary>> ListForOwnerAsync(string ownerUsername, CancellationToken cancellationToken = default);
    Task<MindMapDetail?> GetByIdAsync(string ownerUsername, Guid mindMapId, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string ownerUsername, string title, CancellationToken cancellationToken = default);
    Task RenameAsync(string ownerUsername, Guid mindMapId, string title, CancellationToken cancellationToken = default);
    Task SaveTreeAsync(string ownerUsername, Guid mindMapId, MindMapNode root, CancellationToken cancellationToken = default);
    Task DeleteAsync(string ownerUsername, Guid mindMapId, CancellationToken cancellationToken = default);
}
