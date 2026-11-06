using System.Text.Json;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// Hacker News through Algolia's official HN API (key-less): a story with its comment thread, and search.
public sealed class HackerNewsReach(IHttpClientFactory http) : ReachChannel(http)
{
    private const string Api = "https://hn.algolia.com/api/v1";
    private const int MaxDepth = 3;

    public override string Id => "hackernews";
    public override string Name => "Hacker News";
    public override string Reads => "a story with its comment thread";
    public override string Searches => "stories";

    public override bool Handles(Uri url) =>
        url.Host.Equals("news.ycombinator.com", StringComparison.OrdinalIgnoreCase) && long.TryParse(Query(url, "id"), out _);

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        using var doc = await JsonAsync($"{Api}/items/{long.Parse(Query(url, "id")!, System.Globalization.CultureInfo.InvariantCulture)}", ct);
        var item = doc.RootElement;
        var comments = new List<ReachComment>();
        Collect(item, 0, comments);
        var link = Str(item, "url");
        var facts = new Dictionary<string, string> { ["points"] = Count(Num(item, "points")), ["comments shown"] = comments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (link is not null) facts["links to"] = link;
        var title = Str(item, "title") ?? (Str(item, "type") == "comment" ? "A Hacker News comment" : "Hacker News");
        return new ReachDocument(Name, title, url.ToString(), Limit(Plain(Str(item, "text")), TextCharacters),
            Str(item, "author"), Date(Str(item, "created_at")), comments, facts);
    }

    // Depth-first, so each reply follows the comment it answers.
    private static void Collect(JsonElement item, int depth, List<ReachComment> into)
    {
        if (depth >= MaxDepth || !item.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;
        foreach (var child in children.EnumerateArray())
        {
            if (into.Count >= MaxComments) return;
            var text = Plain(Str(child, "text"));
            if (text.Length == 0) continue;
            into.Add(new ReachComment(Str(child, "author") ?? "someone", Limit(text, CommentCharacters), depth));
            Collect(child, depth + 1, into);
        }
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        using var doc = await JsonAsync($"{Api}/search?query={Uri.EscapeDataString(query)}&tags=story&hitsPerPage=8", ct);
        return doc.RootElement.GetProperty("hits").EnumerateArray().Select(hit =>
        {
            var id = Str(hit, "objectID");
            return new ReachItem(Str(hit, "title") ?? "", $"https://news.ycombinator.com/item?id={id}", Str(hit, "author"), Str(hit, "url"),
                Date(Str(hit, "created_at")), $"{Count(Num(hit, "points"))} points · {Count(Num(hit, "num_comments"))} comments");
        }).ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var _ = await JsonAsync($"{Api}/search?query=ollama&tags=story&hitsPerPage=1", ct);
    }
}
