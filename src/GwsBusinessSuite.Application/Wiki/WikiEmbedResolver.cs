using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Best-effort provider-pattern embedding for the "embed" block: a recognized URL is rewritten
// to that provider's documented embed-URL format and rendered as an iframe instead of a plain
// link. This is NOT full oEmbed - there is no external metadata fetch (title/thumbnail/live
// provider discovery), which is deliberate: a real oEmbed implementation means the server
// fetching an arbitrary provider's oembed endpoint on a URL an editor pasted in, which is an
// SSRF surface this app doesn't currently have anywhere else. Every provider pattern below is
// anchored to a fixed, hardcoded hostname, so resolution never depends on (or fetches) anything
// the editor typed beyond the URL shape itself. A URL from an unlisted or non-matching provider
// always falls back to the existing plain-link render.
//
// Mirrored in wiki-block-editor.js's resolveEmbedUrl() for live-editor preview parity - keep
// both in sync when adding a provider.
public static class WikiEmbedResolver
{
    private sealed record Provider(string Name, Regex Pattern, Func<Match, string, string> BuildEmbedUrl);

    private static readonly IReadOnlyList<Provider> Providers =
    [
        new Provider(
            "YouTube",
            new Regex(@"^https?://(?:www\.)?(?:youtube\.com/watch\?v=|youtube\.com/embed/|youtu\.be/)(?<id>[\w-]{6,})", RegexOptions.IgnoreCase),
            (match, _) => $"https://www.youtube.com/embed/{match.Groups["id"].Value}"),
        new Provider(
            "Vimeo",
            new Regex(@"^https?://(?:www\.)?vimeo\.com/(?<id>\d+)", RegexOptions.IgnoreCase),
            (match, _) => $"https://player.vimeo.com/video/{match.Groups["id"].Value}"),
        new Provider(
            "Spotify",
            new Regex(@"^https?://open\.spotify\.com/(?<type>track|album|playlist|episode|show|artist)/(?<id>[\w]+)", RegexOptions.IgnoreCase),
            (match, _) => $"https://open.spotify.com/embed/{match.Groups["type"].Value}/{match.Groups["id"].Value}"),
        new Provider(
            "Figma",
            new Regex(@"^https?://(?:www\.)?figma\.com/(?:file|proto|design)/[\w-]+/", RegexOptions.IgnoreCase),
            (_, originalUrl) => $"https://www.figma.com/embed?embed_host=sentinel&url={Uri.EscapeDataString(originalUrl.Trim())}"),
        new Provider(
            "CodePen",
            new Regex(@"^https?://codepen\.io/(?<user>[\w-]+)/pen/(?<id>[\w-]+)", RegexOptions.IgnoreCase),
            (match, _) => $"https://codepen.io/{match.Groups["user"].Value}/embed/{match.Groups["id"].Value}?default-tab=result"),
        new Provider(
            "Loom",
            new Regex(@"^https?://(?:www\.)?loom\.com/share/(?<id>[\w]+)", RegexOptions.IgnoreCase),
            (match, _) => $"https://www.loom.com/embed/{match.Groups["id"].Value}")
    ];

    public static bool TryResolve(string? url, out string embedUrl, out string providerLabel)
    {
        embedUrl = string.Empty;
        providerLabel = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        foreach (var provider in Providers)
        {
            var match = provider.Pattern.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            embedUrl = provider.BuildEmbedUrl(match, trimmed);
            providerLabel = provider.Name;
            return true;
        }

        return false;
    }
}
