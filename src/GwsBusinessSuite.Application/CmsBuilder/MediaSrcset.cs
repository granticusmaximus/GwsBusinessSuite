using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Gives every media-library image on a public page a srcset of the resized copies /media/{id}?w=
// serves, so a phone downloads a 640px image rather than the full-size original. Applied once to
// a page's finished HTML, so every widget gets it. Images that already have a srcset are left alone.
public static partial class MediaSrcset
{
    // Content images are at most about the page width; phones get the full viewport width.
    private const string Sizes = "(max-width: 768px) 100vw, 1200px";

    public static string Apply(string html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("/media/", StringComparison.Ordinal)) return html;
        return MediaImage().Replace(html, match =>
        {
            var tag = match.Value;
            if (tag.Contains(" srcset=", StringComparison.OrdinalIgnoreCase)) return tag;
            var id = match.Groups["id"].Value;
            var srcset = string.Join(", ", MediaImageSizes.Widths.Select(width => $"/media/{id}?w={width} {width}w"));
            var extra = $" srcset=\"{srcset}\" sizes=\"{Sizes}\"";
            if (!tag.Contains(" decoding=", StringComparison.OrdinalIgnoreCase)) extra += " decoding=\"async\"";
            // Added just before the closing ">" or "/>".
            var selfClosing = tag.EndsWith("/>", StringComparison.Ordinal);
            var opening = tag[..^(selfClosing ? 2 : 1)].TrimEnd();
            return opening + extra + (selfClosing ? " />" : ">");
        });
    }

    [GeneratedRegex("""<img\b[^>]*?\ssrc="/media/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})"[^>]*>""")]
    private static partial Regex MediaImage();
}
