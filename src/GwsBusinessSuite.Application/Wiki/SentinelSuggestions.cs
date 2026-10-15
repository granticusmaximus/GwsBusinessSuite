using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Suggested edits (Sentinel item 24). Someone in "Suggest" mode edits the page as usual, but
// instead of saving, the edited blocks are compared block by block (by block id) with the
// editor's own copy of the page from when Suggest mode began - not with the stored JSON, which
// the editor may write slightly differently for blocks nobody touched - and stored as changes: a block added (insert), changed (replace) or removed (delete). A
// reviewer with edit access accepts or rejects each change, or all of them; accepting applies the
// change to the page as it is *now*, so other edits made since are kept. Moving a block around is
// not a change - only its content is.
public static class SentinelSuggestionKinds
{
    public const string Insert = "insert";
    public const string Replace = "replace";
    public const string Delete = "delete";
}

public static class SentinelSuggestionStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public const string Open = "open";
    public const string Closed = "closed";
    public const string Withdrawn = "withdrawn";
}

public sealed record SentinelSuggestionChange(
    Guid Id,
    string Kind,
    Guid BlockId,
    // Insert only: the blocks that came before it in the suggested page, nearest first. It goes
    // after the first of these still on the page (or at the top when none are).
    IReadOnlyList<Guid> Anchors,
    // The block as the page had it (replace/delete) and as suggested (insert/replace).
    WikiBlock? Before,
    WikiBlock? Proposed,
    string Status);

public sealed record SentinelSuggestionView(
    Guid Id,
    Guid WikiPageId,
    string Author,
    string Note,
    DateTimeOffset CreatedAt,
    string Status,
    IReadOnlyList<SentinelSuggestionChange> Changes)
{
    public int PendingCount => Changes.Count(change => change.Status == SentinelSuggestionStatuses.Pending);
}

// One word-level piece of a text change, for showing a replace inline.
public sealed record SentinelTextDiffPart(string Text, bool Added, bool Removed);

public interface ISentinelSuggestionService
{
    // Needs comment access. baselineBlocksJson is the editor's copy of the page when Suggest mode
    // began, editedBlocksJson the same editor's copy now; null when nothing changed.
    Task<SentinelSuggestionView?> SubmitAsync(Guid wikiPageId, string baselineBlocksJson, string editedBlocksJson, string? note, string username, CancellationToken cancellationToken = default);

    // Every suggestion still open on the page, oldest first (needs view access).
    Task<IReadOnlyList<SentinelSuggestionView>> ListOpenAsync(Guid wikiPageId, string username, CancellationToken cancellationToken = default);

    // Needs edit access. Applies the accepted changes to the page (one save, one revision) and
    // records the rest as rejected; changes already decided are left alone.
    Task<SentinelSuggestionView> ReviewAsync(Guid suggestionId, IReadOnlyCollection<Guid> changeIds, bool accept, string username, CancellationToken cancellationToken = default);

    // The author takes back a suggestion that's still open.
    Task WithdrawAsync(Guid suggestionId, string username, CancellationToken cancellationToken = default);
}

public static partial class SentinelSuggestions
{
    public const string NotificationKind = "suggestion";
    public const int MaxChanges = 200;
    public const int MaxNoteLength = 1000;
    private const int MaxAnchors = 25;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // current: the stored page now, used for each change's Before (falling back to the baseline
    // copy when the stored page no longer has the block).
    public static IReadOnlyList<SentinelSuggestionChange> Diff(
        IReadOnlyList<WikiBlock> baseline, IReadOnlyList<WikiBlock> edited, IReadOnlyList<WikiBlock> current)
    {
        var baselineById = baseline.GroupBy(block => block.Id).ToDictionary(group => group.Key, group => group.First());
        var currentById = current.GroupBy(block => block.Id).ToDictionary(group => group.Key, group => group.First());
        var editedIds = edited.Select(block => block.Id).ToHashSet();
        var changes = new List<SentinelSuggestionChange>();
        for (var index = 0; index < edited.Count; index++)
        {
            var block = edited[index];
            if (baselineById.TryGetValue(block.Id, out var seen))
            {
                if (!SameContent(seen, block))
                    changes.Add(new(Guid.NewGuid(), SentinelSuggestionKinds.Replace, block.Id, [],
                        currentById.GetValueOrDefault(block.Id) ?? seen, block, SentinelSuggestionStatuses.Pending));
            }
            else
            {
                var anchors = edited.Take(index).Reverse().Select(previous => previous.Id).Take(MaxAnchors).ToList();
                changes.Add(new(Guid.NewGuid(), SentinelSuggestionKinds.Insert, block.Id, anchors, null, block, SentinelSuggestionStatuses.Pending));
            }
        }
        changes.AddRange(baseline
            .Where(block => !editedIds.Contains(block.Id))
            .Select(block => new SentinelSuggestionChange(Guid.NewGuid(), SentinelSuggestionKinds.Delete, block.Id, [],
                currentById.GetValueOrDefault(block.Id) ?? block, null, SentinelSuggestionStatuses.Pending)));
        return changes;
    }

    // The page with the given changes applied, to the page as it is now: a replace of a block
    // that's gone, or a delete of one already removed, does nothing; an insert whose block is
    // already there (accepted twice) isn't added again.
    public static List<WikiBlock> Apply(IReadOnlyList<WikiBlock> current, IEnumerable<SentinelSuggestionChange> changes)
    {
        var blocks = current.ToList();
        foreach (var change in changes)
        {
            var index = blocks.FindIndex(block => block.Id == change.BlockId);
            switch (change.Kind)
            {
                case SentinelSuggestionKinds.Replace when index >= 0 && change.Proposed is not null:
                    blocks[index] = change.Proposed;
                    break;
                case SentinelSuggestionKinds.Delete when index >= 0:
                    blocks.RemoveAt(index);
                    break;
                case SentinelSuggestionKinds.Insert when index < 0 && change.Proposed is not null:
                    var anchor = change.Anchors.Select(id => blocks.FindIndex(block => block.Id == id)).FirstOrDefault(found => found >= 0, -1);
                    blocks.Insert(anchor + 1, change.Proposed);
                    break;
            }
        }
        return blocks;
    }

    // Changed since the suggestion was made: a replace or delete whose block no longer matches
    // what the suggester saw. Shown so the reviewer knows accepting overwrites newer work.
    public static bool IsOutdated(SentinelSuggestionChange change, IReadOnlyList<WikiBlock> current)
    {
        if (change.Kind == SentinelSuggestionKinds.Insert || change.Before is null) return false;
        var now = current.FirstOrDefault(block => block.Id == change.BlockId);
        return now is null || !SameContent(now, change.Before);
    }

    public static string Describe(SentinelSuggestionChange change) => change.Kind switch
    {
        SentinelSuggestionKinds.Insert => "Add",
        SentinelSuggestionKinds.Delete => "Remove",
        _ => "Change"
    };

    // Word-level diff of two texts (longest common subsequence over words).
    public static IReadOnlyList<SentinelTextDiffPart> TextDiff(string before, string after)
    {
        var a = Words().Matches(before).Select(match => match.Value).ToArray();
        var b = Words().Matches(after).Select(match => match.Value).ToArray();
        if ((long)a.Length * b.Length > 250_000)
            return [new(before, false, true), new(after, true, false)];
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = Same(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var parts = new List<SentinelTextDiffPart>();
        void Add(string text, bool added, bool removed)
        {
            if (parts.Count > 0 && parts[^1].Added == added && parts[^1].Removed == removed)
                parts[^1] = parts[^1] with { Text = parts[^1].Text + text };
            else parts.Add(new(text, added, removed));
        }
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (Same(a[x], b[y])) { Add(b[y], false, false); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { Add(a[x], false, true); x++; }
            else { Add(b[y], true, false); y++; }
        }
        while (x < a.Length) Add(a[x++], false, true);
        while (y < b.Length) Add(b[y++], true, false);
        return parts;
    }

    // The last word of a text has no space after it; once more is typed it does. Same word.
    private static bool Same(string left, string right) => left.TrimEnd() == right.TrimEnd();

    public static string BlockText(WikiBlock? block) => block is null ? string.Empty : WikiBlockHtmlRenderer.PlainTextPreview(block, 4000);

    private static bool SameContent(WikiBlock left, WikiBlock right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    // A word with the spaces after it, so a change reads as whole words.
    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex Words();
}
