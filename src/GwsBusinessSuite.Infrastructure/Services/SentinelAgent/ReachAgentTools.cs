using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Services.Reach;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// SentinelGPT's reach outside the app (05-11-26, Agent-Reach's functions written here): read a link
// from the places people share them, search those places, and say which channels are ready. Like
// search_web, only the link or the query goes out, so the same roles that can search the web get it.
public sealed class ReachAgentTools(ReachService reach) : ISentinelAgentToolset
{
    private static readonly string[] ContentRoles = [AppRoles.Admin, AppRoles.Author, AppRoles.Contributor];

    public SentinelAgentArea Area { get; } = new("reach",
        "reading and searching GitHub, YouTube, Hacker News, Reddit, Bluesky, Mastodon, podcasts and X, and reading links",
        ["github", "repository", "youtube", "video", "hacker news", "reddit", "subreddit", "bluesky", "mastodon", "podcast", "episode",
         "tweet", "twitter", "x.com", "http", "www.", "link", "channel"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "read_link",
            "Read a link: a GitHub repository (with its README), issue or pull request (with comments); a YouTube video; a Hacker News story (with comments); a Reddit post (with comments) or subreddit; a Bluesky post (with replies) or profile; a Mastodon post (with replies); an Apple Podcasts show or episode (with its transcript when published); an X post. Any other address is read as a web page.",
            SentinelAgentJson.Schema(("url", "string", "The full https address", true)),
            SentinelAgentRisk.Read, ContentRoles,
            args => $"Reading {Host(args.OptionalText("url"))}",
            ReadAsync),
        new SentinelAgentTool(
            "search_channel",
            "Search one place outside the app: github (repositories; within 'issues' for issues and pull requests), youtube (videos), hackernews (stories), reddit (posts; within a subreddit), bluesky (posts), podcasts (shows; within 'episodes'), x (recent posts). list_channels says which are ready.",
            SentinelAgentJson.Schema(
                ("channel", "string", "github, youtube, hackernews, reddit, bluesky, podcasts or x", true),
                ("query", "string", "What to look for", true),
                ("within", "string", "Narrows it: 'issues' on GitHub, a subreddit on Reddit, 'episodes' for podcasts", false)),
            SentinelAgentRisk.Read, ContentRoles,
            args => $"Searching {args.OptionalText("channel") ?? "a channel"} for \"{args.OptionalText("query")}\"",
            SearchAsync),
        new SentinelAgentTool(
            "list_channels",
            "What SentinelGPT can read and search outside the app, and which channels still need a key to do more.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Read, ContentRoles,
            _ => "Checking which channels are ready",
            ListAsync)
    ];

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var document = await Run(() => reach.ReadAsync(args.Text("url"), ct));
        return SentinelAgentToolResult.Of(new
        {
            channel = document.Channel,
            title = document.Title,
            url = document.Url,
            by = document.Author,
            published = document.Published?.ToLocalGwsDateTime(),
            facts = document.Facts,
            text = document.Text,
            comments = document.Comments?.Select(comment => new { by = comment.Author, text = comment.Text, reply = comment.Depth > 0 ? comment.Depth : (int?)null }),
            note = document.Note
        });
    }

    private async Task<SentinelAgentToolResult> SearchAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var items = await Run(() => reach.SearchAsync(args.Text("channel"), args.Text("query"), args.OptionalText("within"), ct));
        return SentinelAgentToolResult.Of(new
        {
            results = items.Select(item => new
            {
                title = item.Title,
                url = item.Url,
                by = item.Author,
                about = item.Snippet is null ? null : ReachText.Limit(item.Snippet, 300),
                date = item.Published?.ToLocalGwsDate(),
                facts = item.Facts
            }),
            note = items.Count == 0 ? "Nothing matched. Try other words." : null
        });
    }

    private Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct) =>
        Task.FromResult(SentinelAgentToolResult.Of(new
        {
            channels = reach.Describe().Select(channel => new { channel.Id, channel.Name, reads = channel.Reads, searches = channel.Searches, needs = channel.Needs }),
            otherPages = reach.ReadsWebPages ? "Any other link is read as a web page." : "Other web pages can't be read until OllamaWeb:ApiKey is set."
        }));

    // A channel's own explanation reaches the model as a tool error it can act on.
    private static async Task<T> Run<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (ReachException ex)
        {
            throw new SentinelAgentToolException(ex.Message);
        }
    }

    private static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var address) ? address.Host : "a link";
}
