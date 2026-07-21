namespace GwsBusinessSuite.Application.Wiki;

public sealed record WikiBlockMergeResult(bool IsSuccess, string MergedBlocksJson, IReadOnlyList<Guid> ConflictingBlockIds);

public static class WikiBlockMerge
{
    public static WikiBlockMergeResult ThreeWayMerge(string baseJson, string localJson, string remoteJson)
    {
        var baseline = WikiBlockJson.ParseBlocks(baseJson);
        var local = WikiBlockJson.ParseBlocks(localJson);
        var remote = WikiBlockJson.ParseBlocks(remoteJson);
        var baselineById = baseline.ToDictionary(block => block.Id);
        var localById = local.ToDictionary(block => block.Id);
        var remoteById = remote.ToDictionary(block => block.Id);
        var conflicts = new List<Guid>();

        foreach (var baselineBlock in baseline)
        {
            var hasLocal = localById.TryGetValue(baselineBlock.Id, out var localBlock);
            var hasRemote = remoteById.TryGetValue(baselineBlock.Id, out var remoteBlock);
            var localChanged = !hasLocal || !Equivalent(baselineBlock, localBlock!);
            var remoteChanged = !hasRemote || !Equivalent(baselineBlock, remoteBlock!);
            if (localChanged && remoteChanged && (hasLocal != hasRemote || (hasLocal && !Equivalent(localBlock!, remoteBlock!))))
            {
                conflicts.Add(baselineBlock.Id);
            }
        }

        foreach (var localAddition in local.Where(block => !baselineById.ContainsKey(block.Id)))
        {
            if (remoteById.TryGetValue(localAddition.Id, out var remoteAddition) && !Equivalent(localAddition, remoteAddition))
            {
                conflicts.Add(localAddition.Id);
            }
        }

        if (conflicts.Count > 0) return new WikiBlockMergeResult(false, localJson, conflicts.Distinct().ToList());

        var merged = new List<WikiBlock>();
        foreach (var remoteBlock in remote)
        {
            if (!baselineById.TryGetValue(remoteBlock.Id, out var baselineBlock))
            {
                merged.Add(remoteBlock);
                continue;
            }

            if (!localById.TryGetValue(remoteBlock.Id, out var localBlock))
            {
                if (!Equivalent(baselineBlock, remoteBlock)) merged.Add(remoteBlock);
                continue;
            }

            merged.Add(!Equivalent(baselineBlock, localBlock) && Equivalent(baselineBlock, remoteBlock)
                ? localBlock
                : remoteBlock);
        }

        merged.AddRange(local.Where(block => !baselineById.ContainsKey(block.Id) && !remoteById.ContainsKey(block.Id)));
        return new WikiBlockMergeResult(true, WikiBlockJson.Serialize(merged), []);
    }

    private static bool Equivalent(WikiBlock left, WikiBlock right) =>
        string.Equals(WikiBlockJson.Serialize([left]), WikiBlockJson.Serialize([right]), StringComparison.Ordinal);
}
