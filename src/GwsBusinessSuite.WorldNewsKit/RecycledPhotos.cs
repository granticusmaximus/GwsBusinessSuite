using System.Numerics;
using System.Text;

namespace GwsBusinessSuite.WorldNewsKit;

// B3 (13-11-26): recycled photos. A photo in a new story that matches one first seen days earlier
// in a different story is a classic sign of a misleading report (an old flood picture on a new
// flood). Each read article's share image is fingerprinted (a 64-bit difference hash, so resizing,
// recompression and small crops still match) and kept. Content Credentials (C2PA) are noted when
// the file carries them; their signature isn't checked, so it's "carries", not "verified".
public sealed record PhotoPrint(string ArticleId, string EventId, string Outlet, string Title, string ImageUrl, ulong Hash, bool Credentials, DateTimeOffset FirstSeen);

public sealed record RecycledPhoto(PhotoPrint Now, PhotoPrint Earlier);

public static class RecycledPhotos
{
    // Bits that may differ for two prints to count as the same picture.
    public const int MaxDistance = 6;
    // The earlier use must be at least this much older than the story.
    public static readonly TimeSpan MinAge = TimeSpan.FromDays(2);
    // A picture in this many stories or more is a logo or a stock "breaking news" card, not evidence.
    public const int StockEvents = 5;

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static IReadOnlyList<RecycledPhoto> Find(IReadOnlyList<PhotoPrint> story, IReadOnlyList<PhotoPrint> archive, DateTimeOffset storyFirstSeen)
    {
        var found = new List<RecycledPhoto>();
        foreach (var print in story.DistinctBy(p => p.Hash))
        {
            var similar = archive.Where(other => Distance(print.Hash, other.Hash) <= MaxDistance).ToList();
            if (similar.Select(other => other.EventId).Distinct(StringComparer.Ordinal).Count() >= StockEvents) continue;
            // An outlet reusing its own picture (its default share image, its file photo) isn't flagged.
            var earlier = similar
                .Where(other => other.EventId != print.EventId && other.FirstSeen <= storyFirstSeen - MinAge
                    && !string.Equals(other.Outlet, print.Outlet, StringComparison.OrdinalIgnoreCase))
                .MinBy(other => other.FirstSeen);
            if (earlier is not null) found.Add(new RecycledPhoto(print, earlier));
        }
        return found.OrderBy(r => r.Earlier.FirstSeen).ToList();
    }

    // A C2PA manifest lives in a JUMBF box labelled "c2pa" (JPEG APP11, PNG caBX, and others).
    public static bool HasContentCredentials(ReadOnlySpan<byte> file)
    {
        var jumbf = "jumb"u8;
        var c2pa = "c2pa"u8;
        var at = file.IndexOf(jumbf);
        return at >= 0 && file[at..].IndexOf(c2pa) >= 0;
    }

    // The page's share picture: og:image, else twitter:image. Absolute http(s) links only.
    public static string? ShareImage(string html, Uri page)
    {
        foreach (var name in new[] { "og:image:secure_url", "og:image", "twitter:image" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(html,
                $@"<meta\b[^>]*(?:property|name)\s*=\s*[""']{System.Text.RegularExpressions.Regex.Escape(name)}[""'][^>]*>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!match.Success) continue;
            var content = System.Text.RegularExpressions.Regex.Match(match.Value, @"content\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!content.Success) continue;
            var value = System.Net.WebUtility.HtmlDecode(content.Groups[1].Value.Trim());
            if (Uri.TryCreate(page, value, out var image) && image.Scheme is "https" or "http") return image.AbsoluteUri;
        }
        return null;
    }

    public static string Describe(RecycledPhoto photo) =>
        new StringBuilder().Append(photo.Now.Outlet).Append("'s picture was used ")
            .Append((int)(photo.Now.FirstSeen - photo.Earlier.FirstSeen).TotalDays).Append(" days earlier by ")
            .Append(photo.Earlier.Outlet).Append(" (").Append(photo.Earlier.FirstSeen.ToString("dd-MM-yy", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", \"").Append(photo.Earlier.Title).Append("\")").ToString();
}
