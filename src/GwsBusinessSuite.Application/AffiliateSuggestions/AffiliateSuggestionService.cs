using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.AffiliateSuggestions;

public sealed class AffiliateSuggestionService(
    IAppDbContext db,
    IOllamaService ollama,
    IOptions<ContentStudioOptions> options,
    ILogger<AffiliateSuggestionService> logger) : IAffiliateSuggestionService
{
    private const int MaxSuggestionsPerArticle = 3;
    private const int MaxCandidateOffers = 40;
    private const int MaxArticleBodyChars = 3000;

    public async Task<GenerateSuggestionsResult> GenerateForArticleAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var article = await db.Articles
            .Where(x => x.Id == articleId && x.TrashedAt == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (article is null)
        {
            return new GenerateSuggestionsResult { IsSuccess = false, Message = "Article not found." };
        }

        var candidates = await LoadCandidateOffersAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return new GenerateSuggestionsResult
            {
                IsSuccess = false,
                Message = "No affiliate offers are available to suggest from yet. Sync links in CJ Ads Manager first."
            };
        }

        var picks = await PickOffersForArticleAsync(article, candidates, cancellationToken);

        var existingPending = await db.ArticleAffiliateSuggestions
            .Where(x => x.ArticleId == articleId && x.Status == ArticleAffiliateSuggestionStatuses.Pending)
            .ToListAsync(cancellationToken);
        if (existingPending.Count > 0)
        {
            db.ArticleAffiliateSuggestions.RemoveRange(existingPending);
        }

        var created = 0;
        var rank = 1;
        foreach (var pick in picks.Take(MaxSuggestionsPerArticle))
        {
            db.ArticleAffiliateSuggestions.Add(new ArticleAffiliateSuggestion
            {
                ArticleId = articleId,
                AffiliateOfferId = pick.Offer.Id,
                AdvertiserId = pick.Offer.AdvertiserId,
                AdvertiserName = pick.Offer.AdvertiserName,
                LinkName = pick.Offer.LinkName,
                Category = pick.Offer.Category ?? string.Empty,
                TrackingUrl = pick.Offer.TrackingUrl ?? string.Empty,
                Reasoning = pick.Reasoning,
                Rank = rank,
                Status = ArticleAffiliateSuggestionStatuses.Pending,
                CreatedBy = "ai-affiliate-match"
            });
            created += 1;
            rank += 1;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new GenerateSuggestionsResult
        {
            IsSuccess = true,
            Message = created > 0
                ? $"Suggested {created} affiliate offer(s) for \"{article.Title}\"."
                : $"No sufficiently relevant offers were found for \"{article.Title}\".",
            ArticlesProcessed = 1,
            SuggestionsCreated = created
        };
    }

    public async Task<GenerateSuggestionsResult> GenerateForAllArticlesAsync(CancellationToken cancellationToken = default)
    {
        // SQLite can't translate ORDER BY on a DateTimeOffset column, so order
        // client-side after materializing (same pattern used elsewhere in this app).
        var articles = await db.Articles
            .Where(x => x.TrashedAt == null)
            .Select(x => new { x.Id, x.PublishedAt, x.CreatedAt })
            .ToListAsync(cancellationToken);

        var articleIds = articles
            .OrderByDescending(x => x.PublishedAt ?? x.CreatedAt)
            .Select(x => x.Id)
            .ToList();

        var processed = 0;
        var failed = 0;
        var totalCreated = 0;
        var failures = new List<string>();

        foreach (var articleId in articleIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await GenerateForArticleAsync(articleId, cancellationToken);
                if (result.IsSuccess)
                {
                    processed += 1;
                    totalCreated += result.SuggestionsCreated;
                }
                else
                {
                    failed += 1;
                    failures.Add(result.Message);
                }
            }
            catch (Exception ex)
            {
                failed += 1;
                failures.Add($"Article {articleId}: {ex.Message}");
                logger.LogWarning(ex, "Affiliate suggestion generation failed for article {ArticleId}.", articleId);
            }
        }

        return new GenerateSuggestionsResult
        {
            IsSuccess = failed == 0,
            Message = $"Generated suggestions for {processed} of {articleIds.Count} articles ({totalCreated} total suggestions).",
            ArticlesProcessed = processed,
            ArticlesFailed = failed,
            SuggestionsCreated = totalCreated,
            FailureMessages = failures
        };
    }

    public async Task<IReadOnlyList<ArticleSuggestionGroupView>> ListPendingSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        var suggestions = await db.ArticleAffiliateSuggestions
            .AsNoTracking()
            .Where(x => x.Status == ArticleAffiliateSuggestionStatuses.Pending)
            .ToListAsync(cancellationToken);

        if (suggestions.Count == 0)
        {
            return Array.Empty<ArticleSuggestionGroupView>();
        }

        var articleIds = suggestions.Select(x => x.ArticleId).Distinct().ToList();
        var articles = await db.Articles
            .AsNoTracking()
            .Where(x => articleIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return suggestions
            .GroupBy(x => x.ArticleId)
            .Where(group => articles.ContainsKey(group.Key))
            .Select(group =>
            {
                var article = articles[group.Key];
                return new ArticleSuggestionGroupView
                {
                    ArticleId = group.Key,
                    ArticleTitle = article.Title,
                    ArticleSlug = article.Slug,
                    ArticleStatus = article.Status,
                    Suggestions = group
                        .OrderBy(x => x.Rank)
                        .Select(x => new AffiliateSuggestionView
                        {
                            Id = x.Id,
                            AdvertiserId = x.AdvertiserId,
                            AdvertiserName = x.AdvertiserName,
                            LinkName = x.LinkName,
                            Category = x.Category,
                            TrackingUrl = x.TrackingUrl,
                            Reasoning = x.Reasoning,
                            Rank = x.Rank,
                            Status = x.Status
                        })
                        .ToList()
                };
            })
            .OrderByDescending(group => articles[group.ArticleId].PublishedAt ?? articles[group.ArticleId].CreatedAt)
            .ToList();
    }

    public async Task ApplySuggestionAsync(Guid suggestionId, CancellationToken cancellationToken = default)
    {
        var suggestion = await db.ArticleAffiliateSuggestions.FindAsync([suggestionId], cancellationToken);
        if (suggestion is null || suggestion.Status != ArticleAffiliateSuggestionStatuses.Pending)
        {
            return;
        }

        await ApplySuggestionInternalAsync(suggestion, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyAllForArticleAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var pending = await db.ArticleAffiliateSuggestions
            .Where(x => x.ArticleId == articleId && x.Status == ArticleAffiliateSuggestionStatuses.Pending)
            .OrderBy(x => x.Rank)
            .ToListAsync(cancellationToken);

        foreach (var suggestion in pending)
        {
            await ApplySuggestionInternalAsync(suggestion, cancellationToken);
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DismissSuggestionAsync(Guid suggestionId, CancellationToken cancellationToken = default)
    {
        var suggestion = await db.ArticleAffiliateSuggestions.FindAsync([suggestionId], cancellationToken);
        if (suggestion is null)
        {
            return;
        }

        suggestion.Status = ArticleAffiliateSuggestionStatuses.Dismissed;
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        suggestion.UpdatedBy = "admin";
        await db.SaveChangesAsync(cancellationToken);
    }

    // Applies one suggestion: creates the ArticleAffiliatePlacement row AND actually inserts
    // its slot token into the article's markdown (spread across the body by placement count
    // rather than clustered at the end - see InsertSlotToken). Caller is responsible for
    // SaveChangesAsync; kept separate so ApplyAllForArticleAsync can batch 3 inserts into one
    // save instead of three round-trips.
    private async Task ApplySuggestionInternalAsync(ArticleAffiliateSuggestion suggestion, CancellationToken cancellationToken)
    {
        var article = await db.Articles.FindAsync([suggestion.ArticleId], cancellationToken);
        if (article is null)
        {
            suggestion.Status = ArticleAffiliateSuggestionStatuses.Dismissed;
            return;
        }

        var existingPlacementCount = await db.ArticleAffiliatePlacements
            .CountAsync(x => x.ArticleId == article.Id, cancellationToken);

        var slotToken = ArticleMarkdownRenderer.GenerateSlotToken();
        article.BodyMarkdown = InsertSlotToken(article.BodyMarkdown, slotToken, existingPlacementCount);
        article.UpdatedAt = DateTimeOffset.UtcNow;
        article.UpdatedBy = "ai-affiliate-match";

        db.ArticleAffiliatePlacements.Add(new ArticleAffiliatePlacement
        {
            ArticleId = article.Id,
            SlotToken = slotToken,
            AdvertiserId = suggestion.AdvertiserId,
            AdvertiserName = suggestion.AdvertiserName,
            Category = suggestion.Category,
            TrackingUrl = suggestion.TrackingUrl,
            CallToActionText = "Explore Offer",
            SortOrder = existingPlacementCount,
            CreatedBy = "ai-affiliate-match"
        });

        suggestion.Status = ArticleAffiliateSuggestionStatuses.Applied;
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        suggestion.UpdatedBy = "ai-affiliate-match";
    }

    // Placements 0/1/2 land at roughly the 1/4, 1/2, and 3/4 marks of the article (by
    // paragraph, not character count) instead of all clustering at the end - a reader hitting
    // "Explore Offer" cards back-to-back reads as spammy. Falls back to a plain append for
    // very short articles that don't have enough paragraph breaks to spread across.
    private static string InsertSlotToken(string bodyMarkdown, string slotToken, int existingPlacementCount)
    {
        var paragraphs = bodyMarkdown.Split("\n\n", StringSplitOptions.None);
        if (paragraphs.Length < 4)
        {
            return string.IsNullOrWhiteSpace(bodyMarkdown)
                ? slotToken
                : $"{bodyMarkdown.TrimEnd()}\n\n{slotToken}";
        }

        var fraction = existingPlacementCount switch
        {
            0 => 0.25,
            1 => 0.5,
            _ => 0.75
        };

        var insertAt = Math.Clamp((int)Math.Round(paragraphs.Length * fraction), 1, paragraphs.Length - 1);
        var result = new List<string>(paragraphs);
        result.Insert(insertAt, slotToken);
        return string.Join("\n\n", result);
    }

    private async Task<List<AffiliateOffer>> LoadCandidateOffersAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.AffiliateOffers
            .AsNoTracking()
            .Where(x => x.LinkName != x.AdvertiserId) // catalog offers only, not the roster placeholder row
            .Where(x => x.TrackingUrl != null && x.TrackingUrl != string.Empty)
            .Where(x => x.PromotionEndsAt == null || x.PromotionEndsAt >= now)
            .ToListAsync(cancellationToken);

        // SQLite can't translate ORDER BY on a DateTimeOffset column, so order
        // client-side after materializing (same pattern used elsewhere in this app).
        return candidates
            .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
            .Take(MaxCandidateOffers)
            .ToList();
    }

    private async Task<List<(AffiliateOffer Offer, string Reasoning)>> PickOffersForArticleAsync(
        Article article,
        List<AffiliateOffer> candidates,
        CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(options.Value.Model) ? ContentStudioOptions.DefaultModel : options.Value.Model;

        var candidateLines = candidates
            .Select((offer, index) => $"[{index + 1}] {offer.AdvertiserName} - {offer.LinkName} (category: {(string.IsNullOrWhiteSpace(offer.Category) ? "General" : offer.Category)})");

        var bodyExcerpt = article.BodyMarkdown.Length > MaxArticleBodyChars
            ? article.BodyMarkdown[..MaxArticleBodyChars] + "..."
            : article.BodyMarkdown;

        var prompt = $$"""
            You are choosing affiliate ad placements for a blog article. Below is the article,
            followed by a numbered list of available affiliate offers. Choose up to 3 offers
            that are genuinely, topically relevant to THIS SPECIFIC article - not just generically
            popular. If fewer than 3 are truly relevant, choose fewer, or none at all if nothing
            fits. Never invent an offer number that isn't in the list.

            ARTICLE TITLE: {{article.Title}}
            ARTICLE KEYWORDS: {{article.PrimaryKeyword}} {{article.SecondaryKeywords}}
            ARTICLE CONTENT:
            {{bodyExcerpt}}

            AVAILABLE OFFERS:
            {{string.Join('\n', candidateLines)}}

            Respond using exactly this plain-text format, one block per selected offer (zero
            blocks if none are relevant), each block separated by a line containing only ---,
            with no other commentary:

            OFFER_INDEX: <number from the list above>
            REASON: <one sentence on why this offer fits this specific article>
            """;

        var raw = (await ollama.GenerateAsync(model, string.Empty, prompt, cancellationToken)).Trim();
        return ParsePicks(raw, candidates);
    }

    private static List<(AffiliateOffer Offer, string Reasoning)> ParsePicks(string raw, List<AffiliateOffer> candidates)
    {
        var results = new List<(AffiliateOffer, string)>();
        var seen = new HashSet<int>();

        foreach (var block in raw.Split("---", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var indexText = ExtractField(block, "OFFER_INDEX");
            var reason = ExtractField(block, "REASON");

            if (!int.TryParse(indexText, out var index))
            {
                continue;
            }

            var zeroBasedIndex = index - 1;
            if (zeroBasedIndex < 0 || zeroBasedIndex >= candidates.Count || !seen.Add(zeroBasedIndex))
            {
                continue;
            }

            results.Add((candidates[zeroBasedIndex], string.IsNullOrWhiteSpace(reason) ? "Relevant to this article's topic." : reason));

            if (results.Count >= MaxSuggestionsPerArticle)
            {
                break;
            }
        }

        return results;
    }

    private static string ExtractField(string block, string fieldName)
    {
        var prefix = $"{fieldName}:";
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }
}
