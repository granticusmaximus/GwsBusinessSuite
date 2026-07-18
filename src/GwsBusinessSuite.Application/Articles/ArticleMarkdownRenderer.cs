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
        string CallToActionText,
        Guid PlacementId = default,
        string LinkName = "",
        string? ImageUrl = null);

    public static string Render(string markdown, IReadOnlyList<ArticleAffiliatePlacement> placements)
        => Render(markdown, placements.Select(ToMarkup).ToArray());

    public static string Render(
        string markdown,
        IReadOnlyList<ArticleAffiliatePlacement> placements,
        AffiliatePlacementMarkup? rotatingPlacement)
    {
        var rendered = Render(markdown, placements);
        return rotatingPlacement is null
            ? rendered
            : AppendRotatingCard(rendered, rotatingPlacement.Value);
    }

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
        SlotToken: placement.SlotToken,
        AdvertiserName: placement.AdvertiserName,
        Category: placement.Category,
        TrackingUrl: placement.TrackingUrl,
        CallToActionText: placement.CallToActionText,
        PlacementId: placement.Id,
        LinkName: placement.LinkName,
        ImageUrl: placement.ImageUrl);

    private static string BuildCardMarkup(AffiliatePlacementMarkup placement)
    {
        // Advertiser name, link text, the tracking URL, and the image URL all ultimately
        // come from data (CJ sync, manual entry) rather than a trusted constant, so they're
        // HTML-encoded before being embedded — otherwise a crafted value could inject markup
        // into every article that references it.
        var safeAdvertiserName = WebUtility.HtmlEncode(placement.AdvertiserName);

        // The link's own name/text (CJ's LinkName, or whatever the offer was originally
        // titled) is what the card links with - not a synthesized "advertiser + category"
        // phrase. CallToActionText/"Explore Offer" is only a fallback for placements saved
        // before LinkName existed.
        var linkText = !string.IsNullOrWhiteSpace(placement.LinkName)
            ? placement.LinkName
            : (!string.IsNullOrWhiteSpace(placement.CallToActionText) ? placement.CallToActionText : "Explore Offer");
        var safeLinkText = WebUtility.HtmlEncode(linkText);

        // Published articles (real ArticleAffiliatePlacement rows, which have a real Id)
        // route through the /go/{placementId} click-tracking redirect instead of linking
        // straight to the CJ URL. Content Studio draft previews (PlacementId == default,
        // since drafts aren't public and aren't backed by a persisted placement row yet)
        // fall back to the raw tracking URL - there's nothing to track a click against.
        var safeUrl = placement.PlacementId == default
            ? WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(placement.TrackingUrl) ? "#" : placement.TrackingUrl)
            : $"/go/{placement.PlacementId:D}";

        var imageMarkup = string.IsNullOrWhiteSpace(placement.ImageUrl)
            ? string.Empty
            : $"<img src=\"{WebUtility.HtmlEncode(placement.ImageUrl)}\" alt=\"{safeAdvertiserName}\" loading=\"lazy\" style=\"max-width:160px;max-height:120px;object-fit:contain;vertical-align:middle;margin-right:.6rem;\" />";

        return $"<div class=\"cj-ad-card\"><p><strong>Sponsored Pick:</strong> <a href=\"{safeUrl}\" target=\"_blank\" rel=\"noopener noreferrer nofollow sponsored\">{imageMarkup}{safeLinkText}</a></p></div>";
    }

    private static string AppendRotatingCard(string markdown, AffiliatePlacementMarkup placement)
    {
        if (string.IsNullOrWhiteSpace(placement.TrackingUrl))
        {
            return markdown;
        }

        // Automatic rotations are deliberately appended at render time rather than
        // inserting a token into BodyMarkdown. That keeps the editorial source and its
        // revision history unchanged while the durable assignment rotates independently.
        return $"{markdown.TrimEnd()}\n\n{BuildCardMarkup(placement)}";
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
