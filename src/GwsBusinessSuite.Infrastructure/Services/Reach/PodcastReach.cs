using System.Xml;
using System.Xml.Linq;
using GwsBusinessSuite.Application.Abstractions;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// Podcasts: Apple Podcasts' public search and lookup API finds a show and its feed, and the show's
// own RSS feed gives the episodes. When a show publishes a transcript (the podcast:transcript tag),
// an episode link reads it. The feed and transcript live on the show's own host, so they go through
// the open client, which refuses private and reserved addresses.
public sealed class PodcastReach(IHttpClientFactory http) : ReachChannel(http)
{
    private const string Apple = "https://itunes.apple.com";
    private static readonly XNamespace PodcastNs = "https://podcastindex.org/namespace/1.0";
    private const int EpisodesShown = 10;

    public override string Id => "podcasts";
    public override string Name => "Podcasts";
    public override string Reads => "an Apple Podcasts show (latest episodes) or episode (its notes, and the transcript when the show publishes one)";
    public override string Searches => "shows, or episodes (within: episodes)";

    public override bool Handles(Uri url) =>
        url.Host.Equals("podcasts.apple.com", StringComparison.OrdinalIgnoreCase) && ShowId(url) is not null;

    private static string? ShowId(Uri url) =>
        Segments(url).Select(part => part.StartsWith("id", StringComparison.Ordinal) && part.Length > 2 && part[2..].All(char.IsDigit) ? part[2..] : null)
            .LastOrDefault(id => id is not null);

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        var showId = ShowId(url)!;
        var episodeId = Query(url, "i");
        using var lookup = await JsonAsync($"{Apple}/lookup?id={showId}&entity=podcastEpisode&limit=25", ct);
        var results = lookup.RootElement.GetProperty("results").EnumerateArray().ToList();
        var show = results.FirstOrDefault(r => Str(r, "wrapperType") == "track" || Str(r, "kind") == "podcast");
        if (show.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new ReachException("Apple Podcasts doesn't know that show.");
        var showName = Str(show, "collectionName") ?? "A podcast";
        var feedUrl = Str(show, "feedUrl");
        var episodes = results.Where(r => Str(r, "wrapperType") == "podcastEpisode").ToList();

        if (episodeId is null)
        {
            var list = episodes.Take(EpisodesShown).Select(e =>
                $"- {Date(Str(e, "releaseDate"))?.ToGwsDate()}: {Str(e, "trackName")} ({Minutes(Num(e, "trackTimeMillis"))}) {Str(e, "trackViewUrl")}");
            return new ReachDocument(Name, showName, Str(show, "collectionViewUrl") ?? url.ToString(),
                "Latest episodes:\n" + string.Join("\n", list), Str(show, "artistName"),
                Facts: new Dictionary<string, string> { ["genre"] = Str(show, "primaryGenreName") ?? "?", ["episodes"] = Count(Num(show, "trackCount")) });
        }

        var episode = episodes.FirstOrDefault(e => Num(e, "trackId")?.ToString(System.Globalization.CultureInfo.InvariantCulture) == episodeId);
        if (episode.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            throw new ReachException("That episode isn't among the show's recent ones on Apple Podcasts.");
        var title = Str(episode, "trackName") ?? "An episode";
        var notes = Plain(Str(episode, "description"));
        var transcript = feedUrl is null ? null : await TranscriptAsync(feedUrl, title, Str(episode, "episodeUrl"), ct);
        return new ReachDocument(Name, $"{showName}: {title}", Str(episode, "trackViewUrl") ?? url.ToString(),
            Limit(transcript is null ? notes : "Transcript:\n" + transcript, TextCharacters), Str(show, "artistName"), Date(Str(episode, "releaseDate")),
            Facts: new Dictionary<string, string> { ["length"] = Minutes(Num(episode, "trackTimeMillis")) },
            Note: transcript is null ? "The show doesn't publish a transcript for this episode, so these are its notes." : null);
    }

    // The episode's podcast:transcript from the show's feed, matched by its audio address or title.
    private async Task<string?> TranscriptAsync(string feedUrl, string title, string? audioUrl, CancellationToken ct)
    {
        XDocument feed;
        try
        {
            feed = Xml(await TextAsync(feedUrl, ct, client: ReachHttp.Open));
        }
        catch (Exception ex) when (ex is ReachException or HttpRequestException or XmlException)
        {
            return null;
        }
        var item = feed.Descendants("item").FirstOrDefault(i =>
                       audioUrl is not null && i.Element("enclosure")?.Attribute("url")?.Value == audioUrl)
                   ?? feed.Descendants("item").FirstOrDefault(i => string.Equals(i.Element("title")?.Value.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
        var transcripts = item?.Elements(PodcastNs + "transcript").ToList() ?? [];
        // Plain text first, then captions, then HTML.
        var best = transcripts.OrderBy(t => (t.Attribute("type")?.Value ?? "") switch
        {
            "text/plain" => 0,
            "text/vtt" or "application/x-subrip" or "application/srt" => 1,
            "text/html" => 2,
            _ => 3
        }).FirstOrDefault(t => t.Attribute("url") is not null);
        if (best is null) return null;
        try
        {
            var text = await TextAsync(best.Attribute("url")!.Value, ct, client: ReachHttp.Open);
            return (best.Attribute("type")?.Value ?? "") switch
            {
                "text/html" => Plain(text),
                "text/plain" => text.Trim(),
                _ => Captions(text)
            };
        }
        catch (Exception ex) when (ex is ReachException or HttpRequestException)
        {
            return null;
        }
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        var episodes = within?.Trim().ToLowerInvariant() is "episodes" or "episode";
        using var doc = await JsonAsync($"{Apple}/search?media=podcast{(episodes ? "&entity=podcastEpisode" : "")}&limit=8&term={Uri.EscapeDataString(query)}", ct);
        return doc.RootElement.GetProperty("results").EnumerateArray().Select(r => episodes
            ? new ReachItem($"{Str(r, "collectionName")}: {Str(r, "trackName")}", Str(r, "trackViewUrl") ?? "", Str(r, "artistName"),
                Limit(Plain(Str(r, "description")), 200), Date(Str(r, "releaseDate")), Minutes(Num(r, "trackTimeMillis")))
            : new ReachItem(Str(r, "collectionName") ?? "", Str(r, "collectionViewUrl") ?? "", Str(r, "artistName"), null,
                Date(Str(r, "releaseDate")), $"{Str(r, "primaryGenreName")} · {Count(Num(r, "trackCount"))} episodes")).ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var _ = await JsonAsync($"{Apple}/search?media=podcast&limit=1&term=news", ct);
    }

    // Feeds are parsed with DTDs off, so a feed can't pull in other files.
    public static XDocument Xml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static string Minutes(long? milliseconds) => milliseconds is { } ms ? $"{Math.Max(1, ms / 60_000)} min" : "?";
}
