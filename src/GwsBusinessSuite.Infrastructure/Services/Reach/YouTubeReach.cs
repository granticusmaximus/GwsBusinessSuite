using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Options;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// YouTube videos. Without a key, the title and channel come from YouTube's oEmbed service; with
// Reach:YouTubeApiKey the Data API adds the description, length, views and search. There are no
// transcripts: YouTube's API gives captions only to a video's owner, and taking them any other way
// is against its terms.
public sealed partial class YouTubeReach(IHttpClientFactory http, IOptions<ReachOptions> options) : ReachChannel(http)
{
    private const string Api = "https://www.googleapis.com/youtube/v3";
    private const string NoTranscript = "No transcript: YouTube gives captions only to a video's owner.";

    private string? Key => string.IsNullOrWhiteSpace(options.Value.YouTubeApiKey) ? null : options.Value.YouTubeApiKey.Trim();

    public override string Id => "youtube";
    public override string Name => "YouTube";
    public override string Reads => "a video's title and channel (and with the key, its description, length and views)";
    public override string? Searches => Key is null ? null : "videos";
    public override string? Needs => Key is null ? "Reach:YouTubeApiKey (search, and a video's description, length and views)" : null;

    public override bool Handles(Uri url) => VideoId(url) is not null;

    public static string? VideoId(Uri url)
    {
        string? id = null;
        if (url.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = Segments(url).FirstOrDefault();
        else if (HostIs(url, "youtube.com"))
        {
            var parts = Segments(url);
            id = parts.Length >= 2 && parts[0] is "shorts" or "live" or "embed" ? parts[1] : Query(url, "v");
        }
        return id is not null && IdPattern().IsMatch(id) ? id : null;
    }

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        var id = VideoId(url)!;
        var watch = $"https://www.youtube.com/watch?v={id}";
        if (Key is null)
        {
            using var embed = await JsonAsync($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(watch)}&format=json", ct);
            return new ReachDocument(Name, Str(embed.RootElement, "title") ?? "A YouTube video", watch, string.Empty,
                Str(embed.RootElement, "author_name"),
                Note: $"Only the title and channel without Reach:YouTubeApiKey. {NoTranscript}");
        }

        using var doc = await JsonAsync($"{Api}/videos?part=snippet,contentDetails,statistics&id={id}&key={Uri.EscapeDataString(Key)}", ct);
        var video = doc.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault();
        if (video.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new ReachException("That video isn't on YouTube, or it's private.");
        var snippet = Obj(video, "snippet");
        var stats = Obj(video, "statistics");
        var facts = new Dictionary<string, string>
        {
            ["length"] = Length(Str(Obj(video, "contentDetails"), "duration")),
            ["views"] = Count(long.TryParse(Str(stats, "viewCount"), out var views) ? views : null),
            ["likes"] = Count(long.TryParse(Str(stats, "likeCount"), out var likes) ? likes : null)
        };
        return new ReachDocument(Name, Str(snippet, "title") ?? "A YouTube video", watch, Limit(Str(snippet, "description"), TextCharacters),
            Str(snippet, "channelTitle"), Date(Str(snippet, "publishedAt")), Facts: facts, Note: NoTranscript);
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        if (Key is null) return await base.SearchAsync(query, within, ct);
        using var doc = await JsonAsync($"{Api}/search?part=snippet&type=video&maxResults=8&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(Key)}", ct);
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(item =>
        {
            var snippet = Obj(item, "snippet");
            return new ReachItem(Plain(Str(snippet, "title")), $"https://www.youtube.com/watch?v={Str(Obj(item, "id"), "videoId")}",
                Str(snippet, "channelTitle"), Plain(Str(snippet, "description")), Date(Str(snippet, "publishedAt")));
        }).ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var _ = Key is null
            ? await JsonAsync("https://www.youtube.com/oembed?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DjNQXAC9IVRw&format=json", ct)
            : await JsonAsync($"{Api}/videos?part=id&id=jNQXAC9IVRw&key={Uri.EscapeDataString(Key)}", ct);
    }

    // ISO 8601 durations ("PT1H2M3S") as "1 h 02 min".
    public static string Length(string? iso)
    {
        if (iso is null) return "?";
        try
        {
            var span = XmlConvert.ToTimeSpan(iso);
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : $"{span.Minutes} min {span.Seconds:00} s";
        }
        catch (FormatException)
        {
            return iso;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex IdPattern();
}
