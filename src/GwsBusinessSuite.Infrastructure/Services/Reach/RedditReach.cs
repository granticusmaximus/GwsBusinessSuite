using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// Reddit through its official API only. Reddit refuses anonymous readers (403 on 05-11-26), so this
// signs in as an app (client credentials) with Reach:RedditClientId and Reach:RedditClientSecret,
// from a free "script" app at reddit.com/prefs/apps. Until they're set, it says so.
public sealed class RedditReach(IHttpClientFactory http, IOptions<ReachOptions> options, IMemoryCache cache) : ReachChannel(http)
{
    private const string Api = "https://oauth.reddit.com";
    private const string TokenUrl = "https://www.reddit.com/api/v1/access_token";
    // Reddit's rules ask for "<platform>:<app id>:<version>"-style agents.
    private const string Agent = "web:GwsBusinessSuite:1.0 (+https://www.gwsapp.net)";
    private const int MaxDepth = 3;

    private bool Configured => !string.IsNullOrWhiteSpace(options.Value.RedditClientId) && !string.IsNullOrWhiteSpace(options.Value.RedditClientSecret);

    public override string Id => "reddit";
    public override string Name => "Reddit";
    public override string Reads => "a post with its comments, or a subreddit's top posts";
    public override string? Searches => Configured ? "posts (within: a subreddit)" : null;
    public override string? Needs => Configured ? null : "Reach:RedditClientId and Reach:RedditClientSecret (Reddit only answers apps that sign in; a free \"script\" app at reddit.com/prefs/apps)";

    public override bool Handles(Uri url) => HostIs(url, "reddit.com", "redd.it");

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        if (!Configured)
            throw new ReachException("Reddit only answers apps that sign in with their own keys, and none are set yet: Reach:RedditClientId and Reach:RedditClientSecret, from a free \"script\" app at reddit.com/prefs/apps.");
        var parts = Segments(url);
        if (url.Host.EndsWith("redd.it", StringComparison.OrdinalIgnoreCase) && parts.Length >= 1) return await PostAsync(parts[0], ct);
        if (parts.Length >= 4 && parts[0] == "r" && parts[2] == "comments") return await PostAsync(parts[3], ct);
        if (parts.Length >= 2 && parts[0] == "r") return await SubredditAsync(parts[1], ct);
        return null;
    }

    private async Task<ReachDocument> PostAsync(string id, CancellationToken ct)
    {
        using var doc = await RedditAsync($"/comments/{Uri.EscapeDataString(id)}?limit={MaxComments}&depth={MaxDepth}&raw_json=1", ct);
        var listings = doc.RootElement;
        var post = Obj(listings[0].GetProperty("data").GetProperty("children")[0], "data");
        var comments = new List<ReachComment>();
        if (listings.GetArrayLength() > 1) Collect(listings[1], 0, comments);
        var link = Str(post, "url");
        var facts = new Dictionary<string, string>
        {
            ["subreddit"] = "r/" + Str(post, "subreddit"),
            ["score"] = Count(Num(post, "score")),
            ["comments"] = Count(Num(post, "num_comments"))
        };
        if (link is not null && !link.Contains("/comments/", StringComparison.Ordinal)) facts["links to"] = link;
        return new ReachDocument(Name, Str(post, "title") ?? "A Reddit post", "https://www.reddit.com" + Str(post, "permalink"),
            Limit(Str(post, "selftext"), TextCharacters), "u/" + Str(post, "author"), Created(post), comments, facts);
    }

    private async Task<ReachDocument> SubredditAsync(string name, CancellationToken ct)
    {
        using var doc = await RedditAsync($"/r/{Uri.EscapeDataString(name)}/hot?limit=15&raw_json=1", ct);
        var posts = Posts(doc.RootElement).ToList();
        return new ReachDocument(Name, $"r/{name}: hot posts", $"https://www.reddit.com/r/{name}",
            string.Join("\n", posts.Select(p => $"- {p.Title} ({p.Facts}) {p.Url}")));
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        if (!Configured) return await base.SearchAsync(query, within, ct);
        var subreddit = within?.Trim().TrimStart('/').Replace("r/", string.Empty, StringComparison.OrdinalIgnoreCase);
        var path = string.IsNullOrWhiteSpace(subreddit)
            ? $"/search?q={Uri.EscapeDataString(query)}&limit=10&raw_json=1"
            : $"/r/{Uri.EscapeDataString(subreddit)}/search?q={Uri.EscapeDataString(query)}&restrict_sr=1&limit=10&raw_json=1";
        using var doc = await RedditAsync(path, ct);
        return Posts(doc.RootElement).ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        if (!Configured) throw new ReachException("Not set up: needs Reach:RedditClientId and Reach:RedditClientSecret.");
        await TokenAsync(ct);
    }

    private static IEnumerable<ReachItem> Posts(JsonElement listing) =>
        Obj(listing, "data").GetProperty("children").EnumerateArray().Select(child => Obj(child, "data")).Select(p =>
            new ReachItem(Str(p, "title") ?? "", "https://www.reddit.com" + Str(p, "permalink"), "u/" + Str(p, "author"),
                Limit(Str(p, "selftext"), 200), Created(p), $"r/{Str(p, "subreddit")} · {Count(Num(p, "score"))} points · {Count(Num(p, "num_comments"))} comments"));

    private static void Collect(JsonElement listing, int depth, List<ReachComment> into)
    {
        if (depth >= MaxDepth || Obj(listing, "data") is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("children", out var children)) return;
        foreach (var child in children.EnumerateArray())
        {
            if (into.Count >= MaxComments) return;
            if (Str(child, "kind") != "t1") continue;
            var c = Obj(child, "data");
            into.Add(new ReachComment("u/" + Str(c, "author"), Limit(Str(c, "body"), CommentCharacters), depth));
            if (Obj(c, "replies") is { ValueKind: JsonValueKind.Object } replies) Collect(replies, depth + 1, into);
        }
    }

    private static DateTimeOffset? Created(JsonElement post) =>
        post.TryGetProperty("created_utc", out var created) && created.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds((long)created.GetDouble()) : null;

    private async Task<JsonDocument> RedditAsync(string path, CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        return await JsonAsync(Api + path, ct, request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        });
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var key = "reach:reddit:" + options.Value.RedditClientId;
        if (cache.TryGetValue(key, out string? cached) && cached is not null) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")])
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Value.RedditClientId!.Trim()}:{options.Value.RedditClientSecret!.Trim()}")));
        request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        using var doc = JsonDocument.Parse(await SendAsync(request, ct));
        var token = Str(doc.RootElement, "access_token") ?? throw new ReachException("Reddit didn't accept the app's keys. Check Reach:RedditClientId and Reach:RedditClientSecret.");
        var seconds = Num(doc.RootElement, "expires_in") ?? 3600;
        cache.Set(key, token, TimeSpan.FromSeconds(Math.Max(60, seconds - 120)));
        return token;
    }
}
