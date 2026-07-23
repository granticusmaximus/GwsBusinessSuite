namespace GwsBusinessSuite.Application.Wiki;

public readonly record struct SentinelTreeNavigationNode(
    Guid Id,
    Guid? ParentId,
    int SortOrder);

public static class SentinelTreeNavigation
{
    public static IReadOnlyList<Guid> GetVisibleNodeIds(
        IReadOnlyCollection<SentinelTreeNavigationNode> nodes,
        IReadOnlySet<Guid> expandedNodeIds)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        var byParent = nodes.ToLookup(node =>
            node.ParentId is { } parentId && nodeIds.Contains(parentId)
                ? parentId
                : (Guid?)null);
        var visible = new List<Guid>(nodes.Count);
        var visited = new HashSet<Guid>();

        void Visit(Guid? parentId)
        {
            foreach (var node in byParent[parentId]
                         .OrderBy(item => item.SortOrder))
            {
                if (!visited.Add(node.Id))
                {
                    continue;
                }

                visible.Add(node.Id);
                if (expandedNodeIds.Contains(node.Id))
                {
                    Visit(node.Id);
                }
            }
        }

        Visit(null);
        return visible;
    }

    public static IReadOnlySet<Guid> GetBranchNodeIds(
        IReadOnlyCollection<SentinelTreeNavigationNode> nodes)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        return nodes
            .Where(node => node.ParentId is { } parentId && nodeIds.Contains(parentId))
            .Select(node => node.ParentId!.Value)
            .ToHashSet();
    }

    public static IReadOnlyList<Guid> GetAncestorNodeIds(
        Guid nodeId,
        IReadOnlyCollection<SentinelTreeNavigationNode> nodes)
    {
        var byId = nodes.ToDictionary(node => node.Id);
        var ancestors = new List<Guid>();
        var visited = new HashSet<Guid> { nodeId };
        var currentId = nodeId;

        while (byId.TryGetValue(currentId, out var current)
               && current.ParentId is { } parentId
               && visited.Add(parentId))
        {
            ancestors.Add(parentId);
            currentId = parentId;
        }

        return ancestors;
    }
}
