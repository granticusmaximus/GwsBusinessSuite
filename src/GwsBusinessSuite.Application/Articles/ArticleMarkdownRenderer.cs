using System.Text.RegularExpressions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Articles;

/// <summary>
/// Resolves {{CJ_AD_*}} slot tokens in an article's stored markdown into rendered ad-card
/// HTML, using whatever <see cref="ArticleAffiliatePlacement"/> rows currently exist for
/// that article. Resolution happens at read time rather than at publish time, so editing,
/// moving, or removing an ad placement takes effect immediately without republishing.
/// </summary>
public static class ArticleMarkdownRenderer
{
    private static readonly Regex OrphanTokenPattern = new(@"\{\{CJ_AD_[A-Za-z0-9_]+\}\}", RegexOptions.Compiled);

    public static string Render(string markdown, IReadOnlyList<ArticleAffiliatePlacement> placements)
    {
        var rendered = markdown;

        foreach (var placement in placements)
        {
            if (string.IsNullOrWhiteSpace(placement.SlotToken))
            {
                continue;
            }

            rendered = rendered.Replace(placement.SlotToken, BuildCardMarkup(placement), StringComparison.Ordinal);
        }

        // Strip any tokens left over from removed/renamed placements.
        return OrphanTokenPattern.Replace(rendered, string.Empty);
    }

    private static string BuildCardMarkup(ArticleAffiliatePlacement placement)
    {
        var safeCategory = string.IsNullOrWhiteSpace(placement.Category) ? "General" : placement.Category;
        var safeUrl = string.IsNullOrWhiteSpace(placement.TrackingUrl) ? "#" : placement.TrackingUrl;

        return $"<div class=\"cj-ad-card\"><p><strong>Sponsored Pick: {placement.AdvertiserName}</strong></p><p>Category: {safeCategory}</p><p><a href=\"{safeUrl}\" target=\"_blank\" rel=\"noopener noreferrer nofollow sponsored\">{placement.CallToActionText}</a></p></div>";
    }

    /// <summary>
    /// Generates a unique slot token for a new ad placement, distinct from Content
    /// Studio's fixed {{CJ_AD_SLOT_1/2/3}} tokens so any number of ads can be added.
    /// </summary>
    public static string GenerateSlotToken()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{{{{CJ_AD_{suffix}}}}}";
    }
}