using System.Net;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Articles;

/// <summary>
/// Resolves {{CJ_AD_*}} slot tokens in an article or draft's markdown into rendered ad-card
/// HTML. This is the single place that builds ad-card markup: both the live Article pipeline
/// (ArticleAffiliatePlacement, resolved at serve time) and the Content Studio draft pipeline
/// (SeoArticleAffiliatePlacement, resolved at preview/publish time) route through here so
/// there is exactly one HTML-escaping path to keep correct.
/// </summary>
public static class ArticleMarkdownRenderer
{
    private static readonly Regex OrphanTokenPattern = new(@"\{\{CJ_AD_[A-Za-z0-9_]+\}\}", RegexOptions.Compiled);

    public readonly record struct AffiliatePlacementMarkup(
        string SlotToken,
        string AdvertiserName,
        string Category,
        string TrackingUrl,
        string CallToActionText);

    public static string Render(string markdown, IReadOnlyList<ArticleAffiliatePlacement> placements)
        => Render(markdown, placements.Select(ToMarkup).ToArray());

    public static string Render(string markdown, IReadOnlyList<AffiliatePlacementMarkup> placements)
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

    private static AffiliatePlacementMarkup ToMarkup(ArticleAffiliatePlacement placement) => new(
        placement.SlotToken,
        placement.AdvertiserName,
        placement.Category,
        placement.TrackingUrl,
        placement.CallToActionText);

    private static string BuildCardMarkup(AffiliatePlacementMarkup placement)
    {
        // Advertiser/category/CTA text and the tracking URL all ultimately come from data
        // (CJ sync, manual entry) rather than a trusted constant, so they're HTML-encoded
        // before being embedded — otherwise a crafted advertiser name or URL could inject
        // markup into every article that references it.
        var safeAdvertiserName = WebUtility.HtmlEncode(placement.AdvertiserName);
        var safeCategory = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(placement.Category) ? "General" : placement.Category);
        var safeUrl = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(placement.TrackingUrl) ? "#" : placement.TrackingUrl);
        var safeCallToAction = WebUtility.HtmlEncode(placement.CallToActionText);

        return $"<div class=\"cj-ad-card\"><p><strong>Sponsored Pick: {safeAdvertiserName}</strong></p><p>Category: {safeCategory}</p><p><a href=\"{safeUrl}\" target=\"_blank\" rel=\"noopener noreferrer nofollow sponsored\">{safeCallToAction}</a></p></div>";
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