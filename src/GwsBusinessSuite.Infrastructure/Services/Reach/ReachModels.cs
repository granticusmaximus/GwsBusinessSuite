using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// SentinelGPT's reach outside the app (05-11-26). Grant asked for Agent-Reach's ideas and functions,
// not the app: one way for the agent to read and search the places people share links from, a list
// of "channels" saying what each can do, and a check (Agent-Reach's "doctor") showing which work.
// Every channel uses the platform's own API or a page it publishes for readers. Nothing logs in as
// the person, copies a browser session, or gets around a site that says no (RefusedHostPause pauses
// a refusing host). Keys are optional and unlock more where a platform needs one.
public sealed class ReachOptions
{
    public const string SectionName = "Reach";

    // GitHub: raises the limit from 60 to 5,000 requests an hour.
    public string? GitHubToken { get; set; }
    // YouTube Data API: search, and a video's description, length and views.
    public string? YouTubeApiKey { get; set; }
    // Reddit answers only apps that sign in with their own keys (a free "script" app).
    public string? RedditClientId { get; set; }
    public string? RedditClientSecret { get; set; }
    // Bluesky search needs a signed-in session: the account's handle and an app password.
    public string? BlueskyHandle { get; set; }
    public string? BlueskyAppPassword { get; set; }
    // X's paid API: search, replies and numbers. Without it, a post's own text comes from X's embed service.
    public string? XBearerToken { get; set; }
}

public static class ReachHttp
{
    // Fixed, well-known API hosts.
    public const string Fixed = "reach";
    // Hosts the link names (a Mastodon server, a podcast's feed): every connection is checked
    // against private and reserved addresses (AutomationDestinationValidator).
    public const string Open = "reach-open";
    public const string UserAgent = "GwsBusinessSuite-SentinelGPT/1.0 (+https://www.gwsapp.net)";
}

public sealed record ReachComment(string Author, string Text, int Depth = 0);

public sealed record ReachDocument(
    string Channel,
    string Title,
    string Url,
    string Text,
    string? Author = null,
    DateTimeOffset? Published = null,
    IReadOnlyList<ReachComment>? Comments = null,
    IReadOnlyDictionary<string, string>? Facts = null,
    string? Note = null);

public sealed record ReachItem(string Title, string Url, string? Author = null, string? Snippet = null, DateTimeOffset? Published = null, string? Facts = null);

// One line of the channel list and the check.
public sealed record ReachStatus(string Id, string Name, string Reads, string? Searches, string? Needs, bool Checked = false, string? Problem = null)
{
    public bool Working => Checked && Problem is null;
}

public interface IReachChannel
{
    string Id { get; }
    string Name { get; }
    // What links it reads, in a few words.
    string Reads { get; }
    // What it searches, or null when it can't (yet).
    string? Searches { get; }
    // The setting that would unlock more, when it isn't set.
    string? Needs { get; }
    bool Handles(Uri url);
    // Null when the link turned out not to be this channel's after all (the web reader takes it).
    Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct);
    Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct);
    // Throws when the channel doesn't work right now.
    Task CheckAsync(CancellationToken ct);
}

// A problem worth telling the model and the person in plain words.
public sealed class ReachException(string message) : Exception(message);

public static partial class ReachText
{
    public const int TextCharacters = 6_000;
    public const int CommentCharacters = 500;
    public const int MaxComments = 30;

    public static string Plain(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : Spaces().Replace(MailMessageRenderer.HtmlToText(html), " ").Trim();

    public static string Limit(string? text, int characters)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= characters ? value : value[..characters].TrimEnd() + "…";
    }

    // WebVTT or SRT captions as running text: no header, cue numbers, timings or tags.
    public static string Captions(string captions)
    {
        var lines = captions.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line != "WEBVTT" && !line.StartsWith("NOTE", StringComparison.Ordinal)
                           && !line.Contains("-->", StringComparison.Ordinal) && !line.All(char.IsDigit))
            .Select(line => WebUtility.HtmlDecode(Tags().Replace(line, string.Empty)));
        var text = new List<string>();
        foreach (var line in lines)
            if (text.Count == 0 || text[^1] != line) text.Add(line);
        return string.Join(" ", text);
    }

    public static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string Count(long? value) => value is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) : "?";

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
