using System.Net;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Checks before a page goes live (19-11-26): what the page editor's Publish looks at first, from the
// page as it will render (CmsBlockHtmlRenderer) plus its title and meta description. The SEO audit
// scores content; this catches mistakes visitors and search engines would see.
public sealed record PrePublishIssue(string Check, string Message);

public static partial class PrePublishReview
{
    public const int MaxTitle = 60;
    public const int MaxDescription = 160;

    public static IReadOnlyList<PrePublishIssue> Review(string html, string? title, string? metaDescription)
    {
        var issues = new List<PrePublishIssue>();
        if (string.IsNullOrWhiteSpace(title)) issues.Add(new("Title", "The page has no title."));
        else if (title.Trim().Length > MaxTitle) issues.Add(new("Title", $"The title is {title.Trim().Length} characters; search results cut it off after about {MaxTitle}."));
        if (string.IsNullOrWhiteSpace(metaDescription)) issues.Add(new("Description", "The page has no meta description, so search engines pick their own snippet."));
        else if (metaDescription.Trim().Length > MaxDescription) issues.Add(new("Description", $"The meta description is {metaDescription.Trim().Length} characters; it's cut off after about {MaxDescription}."));

        // An image needs alt text; alt="" is allowed (it marks a decorative image on purpose).
        var missingAlt = Image().Matches(html).Count(m => !AltAttribute().IsMatch(m.Value));
        if (missingAlt > 0) issues.Add(new("Images", $"{missingAlt} image{(missingAlt == 1 ? " has" : "s have")} no description (alt text) for screen readers."));

        var levels = Heading().Matches(html).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var h1 = levels.Count(l => l == 1);
        if (h1 == 0) issues.Add(new("Headings", "The page has no main heading (H1)."));
        else if (h1 > 1) issues.Add(new("Headings", $"The page has {h1} main headings (H1); keep one."));
        for (var i = 1; i < levels.Count; i++)
            if (levels[i] > levels[i - 1] + 1)
            {
                issues.Add(new("Headings", $"A heading jumps from H{levels[i - 1]} to H{levels[i]}, skipping a level."));
                break;
            }

        var emptyLinks = Link().Matches(html).Count(m =>
            !Href().Match(m.Value).Success || Href().Match(m.Value).Groups[1].Value.Trim() is "" or "#" or "javascript:void(0)");
        if (emptyLinks > 0) issues.Add(new("Links", $"{emptyLinks} link{(emptyLinks == 1 ? " goes" : "s go")} nowhere (no address, or just #)."));
        var blankText = LinkWithText().Matches(html).Count(m =>
            string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(Tags().Replace(m.Groups[2].Value, ""))) && !m.Groups[1].Value.Contains("aria-label", StringComparison.OrdinalIgnoreCase)
            && !m.Groups[2].Value.Contains("alt=\"", StringComparison.OrdinalIgnoreCase));
        if (blankText > 0) issues.Add(new("Links", $"{blankText} link{(blankText == 1 ? " has" : "s have")} no text a screen reader can say."));
        return issues;
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Image();
    [GeneratedRegex(@"\balt\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex AltAttribute();
    [GeneratedRegex(@"<h([1-6])\b", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();
    [GeneratedRegex(@"<a\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
    [GeneratedRegex(@"\bhref\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();
    [GeneratedRegex(@"<a\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkWithText();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
}
