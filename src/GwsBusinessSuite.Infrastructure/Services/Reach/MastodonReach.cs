using System.Text.RegularExpressions;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// Mastodon (and other servers that speak its API) by a post's link: the post and its replies,
// through the public API of whichever server the link names. The server is the link's own host,
// so these requests go through the open client, which refuses private and reserved addresses on
// every connection. A link that turns out not to be Mastodon goes to the web reader instead.
public sealed partial class MastodonReach(IHttpClientFactory http) : ReachChannel(http)
{
    public override string Id => "mastodon";
    public override string Name => "Mastodon";
    public override string Reads => "a post with its replies, on any server (https://server/@name/123)";

    public override bool Handles(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Contains('.') && PostPath().IsMatch(url.AbsolutePath);

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        var id = PostPath().Match(url.AbsolutePath).Groups["id"].Value;
        var server = $"https://{url.Authority}";
        System.Text.Json.JsonDocument status;
        try
        {
            status = await JsonAsync($"{server}/api/v1/statuses/{id}", ct, client: ReachHttp.Open);
        }
        catch (ReachException)
        {
            return null;
        }
        using (status)
        {
            var post = status.RootElement;
            if (Str(post, "id") is null) return null;
            var replies = new List<ReachComment>();
            try
            {
                using var context = await JsonAsync($"{server}/api/v1/statuses/{id}/context", ct, client: ReachHttp.Open);
                if (context.RootElement.TryGetProperty("descendants", out var descendants))
                    replies.AddRange(descendants.EnumerateArray().Take(MaxComments).Select(reply =>
                        new ReachComment("@" + Str(Obj(reply, "account"), "acct"), Limit(Plain(Str(reply, "content")), CommentCharacters))));
            }
            catch (ReachException)
            {
                // Some servers only show replies to signed-in readers.
            }
            var account = Obj(post, "account");
            return new ReachDocument(Name, $"A post by @{Str(account, "acct")}", Str(post, "url") ?? url.ToString(),
                Plain(Str(post, "content")), Str(account, "display_name") is { Length: > 0 } display ? display : "@" + Str(account, "acct"),
                Date(Str(post, "created_at")), replies,
                new Dictionary<string, string>
                {
                    ["server"] = url.Host,
                    ["favourites"] = Count(Num(post, "favourites_count")),
                    ["boosts"] = Count(Num(post, "reblogs_count")),
                    ["replies"] = Count(Num(post, "replies_count"))
                });
        }
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var _ = await JsonAsync("https://mastodon.social/api/v1/instance", ct, client: ReachHttp.Open);
    }

    // /@name/123 and /users/name/statuses/123
    [GeneratedRegex(@"^/(@[^/]+|users/[^/]+/statuses)/(?<id>\d{6,})/?$")]
    private static partial Regex PostPath();
}
