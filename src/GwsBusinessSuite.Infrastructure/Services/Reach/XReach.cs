using System.Text.Json;
using Microsoft.Extensions.Options;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// X (Twitter) posts. Without a key, a post's own text comes from X's public embed (oEmbed)
// service. X's API is paid, so search, replies and the numbers wait for Reach:XBearerToken.
public sealed class XReach(IHttpClientFactory http, IOptions<ReachOptions> options) : ReachChannel(http)
{
    private const string Api = "https://api.x.com/2";
    private const string Embed = "https://publish.x.com/oembed";

    private string? Token => string.IsNullOrWhiteSpace(options.Value.XBearerToken) ? null : options.Value.XBearerToken.Trim();

    public override string Id => "x";
    public override string Name => "X";
    public override string Reads => "a post's text (and with the key, its replies and numbers)";
    public override string? Searches => Token is null ? null : "recent posts (the last 7 days)";
    public override string? Needs => Token is null ? "Reach:XBearerToken (X's paid API: search, replies and numbers)" : null;

    public override bool Handles(Uri url) => Post(url) is not null;

    // x.com/{user}/status/{id}, also on twitter.com.
    public static (string User, string Id)? Post(Uri url)
    {
        if (!HostIs(url, "x.com", "twitter.com")) return null;
        var parts = Segments(url);
        return parts.Length >= 3 && parts[1] is "status" or "statuses" && parts[2].All(char.IsDigit) && parts[2].Length > 0
            ? (parts[0], parts[2]) : null;
    }

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        var (user, id) = Post(url)!.Value;
        var link = $"https://x.com/{user}/status/{id}";
        if (Token is null)
        {
            using var embed = await JsonAsync($"{Embed}?url={Uri.EscapeDataString(link)}&omit_script=true&dnt=true", ct);
            return new ReachDocument(Name, $"A post by @{user}", link, Plain(Str(embed.RootElement, "html")), Str(embed.RootElement, "author_name"),
                Note: "The post's own text, from X's embed service. Replies and numbers need Reach:XBearerToken.");
        }

        using var doc = await JsonAsync($"{Api}/tweets/{id}?tweet.fields=created_at,public_metrics,conversation_id&expansions=author_id&user.fields=username,name", ct, Authorize);
        var tweet = Obj(doc.RootElement, "data");
        var authors = Authors(doc.RootElement);
        var metrics = Obj(tweet, "public_metrics");
        var replies = new List<ReachComment>();
        try
        {
            using var thread = await JsonAsync($"{Api}/tweets/search/recent?query={Uri.EscapeDataString("conversation_id:" + (Str(tweet, "conversation_id") ?? id))}&max_results=20&expansions=author_id&user.fields=username", ct, Authorize);
            var people = Authors(thread.RootElement);
            if (thread.RootElement.TryGetProperty("data", out var data))
                replies.AddRange(data.EnumerateArray().Select(reply => new ReachComment(
                    "@" + (people.GetValueOrDefault(Str(reply, "author_id") ?? "") ?? "someone"), Limit(Str(reply, "text"), CommentCharacters))));
        }
        catch (ReachException)
        {
            // Replies are only searchable for a week, and some plans can't search at all.
        }
        return new ReachDocument(Name, $"A post by @{authors.GetValueOrDefault(Str(tweet, "author_id") ?? "") ?? user}", link, Str(tweet, "text") ?? "",
            "@" + user, Date(Str(tweet, "created_at")), replies,
            new Dictionary<string, string>
            {
                ["likes"] = Count(Num(metrics, "like_count")),
                ["reposts"] = Count(Num(metrics, "retweet_count")),
                ["replies"] = Count(Num(metrics, "reply_count"))
            });
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        if (Token is null) return await base.SearchAsync(query, within, ct);
        using var doc = await JsonAsync($"{Api}/tweets/search/recent?query={Uri.EscapeDataString(query)}&max_results=10&tweet.fields=created_at,public_metrics&expansions=author_id&user.fields=username", ct, Authorize);
        var people = Authors(doc.RootElement);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
        return data.EnumerateArray().Select(post =>
        {
            var user = people.GetValueOrDefault(Str(post, "author_id") ?? "") ?? "i";
            return new ReachItem(Limit(Str(post, "text"), 120), $"https://x.com/{user}/status/{Str(post, "id")}", "@" + user, Str(post, "text"),
                Date(Str(post, "created_at")), $"{Count(Num(Obj(post, "public_metrics"), "like_count"))} likes");
        }).ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var _ = Token is null
            ? await JsonAsync($"{Embed}?url={Uri.EscapeDataString("https://x.com/jack/status/20")}&omit_script=true", ct)
            : await JsonAsync($"{Api}/tweets/20", ct, Authorize);
    }

    private static Dictionary<string, string> Authors(JsonElement root)
    {
        var people = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Obj(root, "includes") is { ValueKind: JsonValueKind.Object } includes && includes.TryGetProperty("users", out var users))
            foreach (var user in users.EnumerateArray())
                if (Str(user, "id") is { } id && Str(user, "username") is { } name) people[id] = name;
        return people;
    }

    private void Authorize(HttpRequestMessage request) => request.Headers.Authorization = new("Bearer", Token);
}
