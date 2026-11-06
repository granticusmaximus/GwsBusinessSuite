using System.Net.Http.Json;
using GwsBusinessSuite.Application.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// Bluesky through its public AppView: a post's thread or a profile's latest posts, no account needed.
// Search needs a signed-in session (403 without one on 05-11-26), from Reach:BlueskyHandle and an
// app password (Settings > Privacy and security > App passwords), never the account's own password.
public sealed class BlueskyReach(IHttpClientFactory http, IOptions<ReachOptions> options, IMemoryCache cache) : ReachChannel(http)
{
    private const string PublicApi = "https://public.api.bsky.app/xrpc";
    private const string SignedInApi = "https://bsky.social/xrpc";
    private const int MaxDepth = 3;

    private bool Configured => !string.IsNullOrWhiteSpace(options.Value.BlueskyHandle) && !string.IsNullOrWhiteSpace(options.Value.BlueskyAppPassword);

    public override string Id => "bluesky";
    public override string Name => "Bluesky";
    public override string Reads => "a post with its replies, or a profile's latest posts";
    public override string? Searches => Configured ? "posts" : null;
    public override string? Needs => Configured ? null : "Reach:BlueskyHandle and Reach:BlueskyAppPassword (search; use an app password, not the account's own)";

    public override bool Handles(Uri url) =>
        url.Host.Equals("bsky.app", StringComparison.OrdinalIgnoreCase) && Segments(url) is ["profile", _, ..];

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        var parts = Segments(url);
        var actor = parts[1];
        return parts.Length >= 4 && parts[2] == "post" ? await ThreadAsync(actor, parts[3], ct) : await ProfileAsync(actor, ct);
    }

    private async Task<ReachDocument> ThreadAsync(string actor, string key, CancellationToken ct)
    {
        var uri = $"at://{actor}/app.bsky.feed.post/{key}";
        using var doc = await JsonAsync($"{PublicApi}/app.bsky.feed.getPostThread?uri={Uri.EscapeDataString(uri)}&depth={MaxDepth}&parentHeight=0", ct);
        var thread = Obj(doc.RootElement, "thread");
        var post = Obj(thread, "post");
        var replies = new List<ReachComment>();
        Collect(thread, 0, replies);
        return new ReachDocument(Name, $"A post by @{Str(Obj(post, "author"), "handle") ?? actor}", Link(post) ?? $"https://bsky.app/profile/{actor}/post/{key}",
            Str(Obj(post, "record"), "text") ?? "", Str(Obj(post, "author"), "displayName") ?? "@" + actor, Date(Str(Obj(post, "record"), "createdAt")), replies,
            new Dictionary<string, string>
            {
                ["likes"] = Count(Num(post, "likeCount")),
                ["reposts"] = Count(Num(post, "repostCount")),
                ["replies"] = Count(Num(post, "replyCount"))
            });
    }

    private async Task<ReachDocument> ProfileAsync(string actor, CancellationToken ct)
    {
        using var profile = await JsonAsync($"{PublicApi}/app.bsky.actor.getProfile?actor={Uri.EscapeDataString(actor)}", ct);
        using var feed = await JsonAsync($"{PublicApi}/app.bsky.feed.getAuthorFeed?actor={Uri.EscapeDataString(actor)}&limit=15&filter=posts_no_replies", ct);
        var p = profile.RootElement;
        var posts = feed.RootElement.GetProperty("feed").EnumerateArray().Select(item => Obj(item, "post"))
            .Select(post => $"- {Date(Str(Obj(post, "record"), "createdAt"))?.ToGwsDate()}: {Limit(Str(Obj(post, "record"), "text"), 280)} {Link(post)}");
        return new ReachDocument(Name, $"{Str(p, "displayName") ?? actor} (@{Str(p, "handle") ?? actor})", $"https://bsky.app/profile/{actor}",
            (Str(p, "description") is { Length: > 0 } about ? about + "\n\n" : "") + "Latest posts:\n" + string.Join("\n", posts),
            "@" + (Str(p, "handle") ?? actor),
            Facts: new Dictionary<string, string> { ["followers"] = Count(Num(p, "followersCount")), ["posts"] = Count(Num(p, "postsCount")) });
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        if (!Configured) return await base.SearchAsync(query, within, ct);
        var session = await SessionAsync(ct);
        using var doc = await JsonAsync($"{SignedInApi}/app.bsky.feed.searchPosts?q={Uri.EscapeDataString(query)}&limit=10", ct,
            request => request.Headers.Authorization = new("Bearer", session));
        return doc.RootElement.GetProperty("posts").EnumerateArray().Select(post => new ReachItem(
            Limit(Str(Obj(post, "record"), "text"), 120), Link(post) ?? "", "@" + Str(Obj(post, "author"), "handle"),
            Str(Obj(post, "record"), "text"), Date(Str(Obj(post, "record"), "createdAt")), $"{Count(Num(post, "likeCount"))} likes")).ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var _ = await JsonAsync($"{PublicApi}/app.bsky.actor.getProfile?actor=bsky.app", ct);
        if (Configured) await SessionAsync(ct);
    }

    private static void Collect(JsonElement thread, int depth, List<ReachComment> into)
    {
        if (depth >= MaxDepth || !thread.TryGetProperty("replies", out var replies) || replies.ValueKind != JsonValueKind.Array) return;
        foreach (var reply in replies.EnumerateArray())
        {
            if (into.Count >= MaxComments) return;
            var post = Obj(reply, "post");
            if (Str(Obj(post, "record"), "text") is not { Length: > 0 } text) continue;
            into.Add(new ReachComment("@" + Str(Obj(post, "author"), "handle"), Limit(text, CommentCharacters), depth));
            Collect(reply, depth + 1, into);
        }
    }

    // https://bsky.app/profile/{handle}/post/{key}, from the post's at:// address.
    private static string? Link(JsonElement post)
    {
        var uri = Str(post, "uri");
        var handle = Str(Obj(post, "author"), "handle");
        return uri is null || handle is null ? null : $"https://bsky.app/profile/{handle}/post/{uri[(uri.LastIndexOf('/') + 1)..]}";
    }

    private async Task<string> SessionAsync(CancellationToken ct)
    {
        var key = "reach:bluesky:" + options.Value.BlueskyHandle;
        if (cache.TryGetValue(key, out string? cached) && cached is not null) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SignedInApi}/com.atproto.server.createSession")
        {
            Content = JsonContent.Create(new { identifier = options.Value.BlueskyHandle!.Trim(), password = options.Value.BlueskyAppPassword!.Trim() })
        };
        using var doc = JsonDocument.Parse(await SendAsync(request, ct));
        var token = Str(doc.RootElement, "accessJwt") ?? throw new ReachException("Bluesky didn't accept the handle and app password.");
        // Access tokens last about two hours.
        cache.Set(key, token, TimeSpan.FromMinutes(90));
        return token;
    }

    protected override ReachException Refusal(System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.Unauthorized
            ? new ReachException("Bluesky didn't accept the handle and app password. Check Reach:BlueskyHandle and Reach:BlueskyAppPassword.")
            : base.Refusal(status);
}
