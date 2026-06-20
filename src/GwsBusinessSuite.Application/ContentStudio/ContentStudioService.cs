using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class ContentStudioService(
    IAppDbContext db,
    IAppDbContextFactory dbContextFactory,
    IOllamaService ollama,
    IAffiliateOfferScoringService offerScoringService,
    IOptions<ContentStudioOptions> options,
    ILogger<ContentStudioService> logger) : IContentStudioService
{
    private static readonly string[] AffiliateSlotTokens = ["{{CJ_AD_SLOT_1}}", "{{CJ_AD_SLOT_2}}", "{{CJ_AD_SLOT_3}}"];

    public async Task<IReadOnlyList<ContentStudioDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default)
    {
        var drafts = await db.SeoArticleDrafts
            .AsNoTracking()
            .Select(x => new ContentStudioDraftSummary
            {
                DraftId = x.Id,
                Topic = x.Topic,
                Title = x.Title,
                Status = x.Status,
                RevisionNumber = x.RevisionNumber,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return drafts
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToList();
    }

    public Task<ArticleGenerationResult?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        => GetDraftCoreAsync(db, draftId, cancellationToken);

    private async Task<ArticleGenerationResult?> GetDraftCoreAsync(IAppDbContext context, Guid draftId, CancellationToken cancellationToken)
    {
        await RecordDraftImpressionsAsync(context, draftId, "content-studio-preview", cancellationToken);

        var draft = await LoadDraftSnapshotAsync(context, draftId, cancellationToken);

        if (draft is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var currentWindowStart = now.AddDays(-7);
        var previousWindowStart = now.AddDays(-14);

        var interactions = await context.SeoArticleAffiliateInteractions
            .AsNoTracking()
            .Where(x => x.SeoArticleDraftId == draftId)
            .Select(x => new { x.SlotToken, x.EventType, x.CreatedAt })
            .ToListAsync(cancellationToken);

        var metricsBySlot = interactions
            .GroupBy(x => x.SlotToken, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var impressions = group.Count(x => x.EventType == AffiliateInteractionEventTypes.Impression);
                    var clicks = group.Count(x => x.EventType == AffiliateInteractionEventTypes.Click);

                    var currentImpressions = group.Count(x =>
                        x.EventType == AffiliateInteractionEventTypes.Impression &&
                        x.CreatedAt >= currentWindowStart);
                    var currentClicks = group.Count(x =>
                        x.EventType == AffiliateInteractionEventTypes.Click &&
                        x.CreatedAt >= currentWindowStart);

                    var previousImpressions = group.Count(x =>
                        x.EventType == AffiliateInteractionEventTypes.Impression &&
                        x.CreatedAt >= previousWindowStart &&
                        x.CreatedAt < currentWindowStart);
                    var previousClicks = group.Count(x =>
                        x.EventType == AffiliateInteractionEventTypes.Click &&
                        x.CreatedAt >= previousWindowStart &&
                        x.CreatedAt < currentWindowStart);

                    var currentCtr = CalculateCtr(currentClicks, currentImpressions);
                    var previousCtr = CalculateCtr(previousClicks, previousImpressions);

                    return (
                        Impressions: impressions,
                        Clicks: clicks,
                        CurrentCtr7Day: currentCtr,
                        PreviousCtr7Day: previousCtr,
                        Delta7Day: currentCtr - previousCtr);
                },
                StringComparer.Ordinal);

        return MapDraft(draft, metricsBySlot, GetAuthorName(), GetSiteBaseUrl());
    }

    public async Task RecordAffiliatePlacementInteractionAsync(
        AffiliatePlacementInteractionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DraftId == Guid.Empty)
        {
            throw new ArgumentException("Draft ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.SlotToken))
        {
            throw new ArgumentException("Slot token is required.", nameof(request));
        }

        if (!string.Equals(request.EventType, AffiliateInteractionEventTypes.Impression, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.EventType, AffiliateInteractionEventTypes.Click, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Event type must be Impression or Click.", nameof(request));
        }

        var placement = await db.SeoArticleAffiliatePlacements
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.SeoArticleDraftId == request.DraftId && x.SlotToken == request.SlotToken,
                cancellationToken);

        if (placement is null)
        {
            return;
        }

        db.SeoArticleAffiliateInteractions.Add(new SeoArticleAffiliateInteraction
        {
            SeoArticleDraftId = placement.SeoArticleDraftId,
            SlotToken = placement.SlotToken,
            AdvertiserId = placement.AdvertiserId,
            EventType = request.EventType,
            CreatedBy = request.PerformedBy
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ArticleGenerationResult> GenerateArticleAsync(
        ArticleGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Topic);

        var scoredOffers = await offerScoringService.ScoreOffersAsync(request, maxOffers: AffiliateSlotTokens.Length, cancellationToken);
        var prompt = BuildPrompt(request, scoredOffers);
        var configuredOptions = options.Value;
        var model = string.IsNullOrWhiteSpace(configuredOptions.Model)
            ? ContentStudioOptions.DefaultModel
            : configuredOptions.Model;

        logger.LogInformation(
            "Generating Content Studio draft for topic '{Topic}' using Ollama model '{Model}'.",
            request.Topic,
            model);

        // Ollama's first-time model load plus generation can take several minutes.
        // A Blazor Server circuit can disconnect and have its DI scope (and the
        // constructor-injected db context) disposed during that wait, so the save
        // below uses a freshly created context instead of the field-level `db`.
        var markdown = (await ollama.GenerateAsync(model, string.Empty, prompt, cancellationToken)).Trim();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            logger.LogWarning("Ollama returned an empty draft for topic '{Topic}'.", request.Topic);
            throw new InvalidOperationException("Ollama returned an empty draft. Try again or switch models.");
        }

        var markdownWithAffiliateSlots = EnsureAffiliatePlaceholders(markdown);
        var slug = CreateSlug(request.Topic);
        var title = request.Topic.Trim();

        var draft = new SeoArticleDraft
        {
            Topic = request.Topic.Trim(),
            TargetAudience = request.TargetAudience.Trim(),
            PrimaryKeyword = request.PrimaryKeyword.Trim(),
            SecondaryKeywords = request.SecondaryKeywords.Trim(),
            Status = SeoArticleDraftStatuses.PendingReview,
            Title = title,
            MetaDescription = BuildMetaDescription(request),
            Slug = slug,
            EstimatedReadingTime = "8-10 minutes",
            OutlineMarkdown = ExtractSection(markdownWithAffiliateSlots, "## Outline"),
            ArticleMarkdown = markdownWithAffiliateSlots,
            SeoChecklistMarkdown = BuildDefaultChecklist(request.PrimaryKeyword, request.SecondaryKeywords),
            SourceNotesMarkdown = "Verify references against Microsoft Learn and official .NET docs before publishing.",
            RevisionNumber = 0
        };

        await using var freshContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        freshContext.SeoArticleDrafts.Add(draft);
        foreach (var placement in BuildAffiliatePlacements(draft.Id, scoredOffers))
        {
            freshContext.SeoArticleAffiliatePlacements.Add(placement);
        }

        freshContext.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = draft.Id,
            EventType = SeoArticleWorkflowEventTypes.Generated,
            Notes = "Draft generated from article brief.",
            CreatedBy = "content-studio"
        });

        await freshContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Content Studio draft {DraftId} generated successfully for topic '{Topic}' with slug '{Slug}'.",
            draft.Id,
            request.Topic,
            slug);

        var saved = await GetDraftCoreAsync(freshContext, draft.Id, cancellationToken);
        if (saved is null)
        {
            throw new InvalidOperationException("The generated draft could not be reloaded from persistence.");
        }

        return saved;
    }

    public async Task<ArticleGenerationResult?> RequestRevisionAsync(
        DraftRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var draft = await db.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var revisionNotes = request.RequestedModifications.Trim();
        if (string.IsNullOrWhiteSpace(revisionNotes))
        {
            throw new ArgumentException("Revision request cannot be empty.", nameof(request));
        }

        var baseRequest = new ArticleGenerationRequest
        {
            Topic = draft.Topic,
            TargetAudience = draft.TargetAudience,
            PrimaryKeyword = draft.PrimaryKeyword,
            SecondaryKeywords = draft.SecondaryKeywords
        };

        var scoredOffers = await offerScoringService.ScoreOffersAsync(baseRequest, maxOffers: AffiliateSlotTokens.Length, cancellationToken);
        var prompt = BuildPrompt(baseRequest, scoredOffers) + $"\n\nRequested revisions:\n- {revisionNotes}";

        var configuredModel = string.IsNullOrWhiteSpace(options.Value.Model)
            ? ContentStudioOptions.DefaultModel
            : options.Value.Model;

        // Ollama generation can take several minutes; a Blazor Server circuit can
        // disconnect and dispose the field-level db context during that wait, so the
        // save below re-loads the draft on a freshly created context instead of
        // reusing the entity tracked by the original context.
        var revisedMarkdown = (await ollama.GenerateAsync(configuredModel, string.Empty, prompt, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(revisedMarkdown))
        {
            throw new InvalidOperationException("Ollama returned an empty revised draft.");
        }

        var revisedMarkdownWithAffiliateSlots = EnsureAffiliatePlaceholders(revisedMarkdown);

        await using var freshContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var freshDraft = await freshContext.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == draft.Id, cancellationToken);
        if (freshDraft is null)
        {
            return null;
        }

        freshDraft.ArticleMarkdown = revisedMarkdownWithAffiliateSlots;
        freshDraft.OutlineMarkdown = ExtractSection(revisedMarkdownWithAffiliateSlots, "## Outline");
        freshDraft.RequestedModifications = revisionNotes;
        freshDraft.Status = SeoArticleDraftStatuses.PendingReview;
        freshDraft.RevisionNumber += 1;
        freshDraft.UpdatedAt = DateTimeOffset.UtcNow;
        freshDraft.UpdatedBy = request.PerformedBy;

        var existingPlacements = await freshContext.SeoArticleAffiliatePlacements
            .Where(x => x.SeoArticleDraftId == freshDraft.Id)
            .ToListAsync(cancellationToken);

        foreach (var existingPlacement in existingPlacements)
        {
            freshContext.SeoArticleAffiliatePlacements.Remove(existingPlacement);
        }

        foreach (var placement in BuildAffiliatePlacements(freshDraft.Id, scoredOffers))
        {
            freshContext.SeoArticleAffiliatePlacements.Add(placement);
        }

        freshContext.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = freshDraft.Id,
            EventType = SeoArticleWorkflowEventTypes.Revised,
            Notes = revisionNotes,
            CreatedBy = request.PerformedBy
        });

        await freshContext.SaveChangesAsync(cancellationToken);

        return await GetDraftCoreAsync(freshContext, freshDraft.Id, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> ApproveDraftAsync(
        DraftDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ApplyDecisionAsync(request, SeoArticleDraftStatuses.Approved, SeoArticleWorkflowEventTypes.Approved, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> PublishDraftToSiteAsync(
        DraftPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        var draft = await db.SeoArticleDrafts
            .FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);

        if (draft is null)
        {
            return null;
        }

        if (!string.Equals(draft.Status, SeoArticleDraftStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only approved drafts can be published live to the site.");
        }

        var heroImageUrl = string.IsNullOrWhiteSpace(draft.HeroImageDataUri)
            ? null
            : $"/og-image/{draft.Slug}";

        var existing = await db.Articles
            .FirstOrDefaultAsync(article => article.Slug == draft.Slug, cancellationToken);

        if (existing is null)
        {
            db.Articles.Add(new Article
            {
                Slug = draft.Slug,
                Title = draft.Title,
                Topic = draft.Topic,
                BodyMarkdown = draft.ArticleMarkdown,
                MetaDescription = draft.MetaDescription,
                PrimaryKeyword = draft.PrimaryKeyword,
                SecondaryKeywords = draft.SecondaryKeywords,
                Author = "Grant Watson",
                EstimatedReadingTime = draft.EstimatedReadingTime,
                HeroImageUrl = heroImageUrl,
                HeroImageDataUri = draft.HeroImageDataUri,
                HeroImageAltText = draft.HeroImageAltText,
                HeroImageCaption = draft.HeroImageCaption,
                Status = ArticleStatuses.Published,
                Source = ArticleSource.OllamaGenerated,
                PublishedAt = DateTimeOffset.UtcNow,
                SourceDraftId = draft.Id,
                CreatedBy = request.PerformedBy
            });
        }
        else
        {
            existing.Title = draft.Title;
            existing.Topic = draft.Topic;
            existing.BodyMarkdown = draft.ArticleMarkdown;
            existing.MetaDescription = draft.MetaDescription;
            existing.PrimaryKeyword = draft.PrimaryKeyword;
            existing.SecondaryKeywords = draft.SecondaryKeywords;
            existing.Author = "Grant Watson";
            existing.EstimatedReadingTime = draft.EstimatedReadingTime;
            existing.HeroImageUrl = heroImageUrl;
            existing.HeroImageDataUri = draft.HeroImageDataUri;
            existing.HeroImageAltText = draft.HeroImageAltText;
            existing.HeroImageCaption = draft.HeroImageCaption;
            existing.Status = ArticleStatuses.Published;
            existing.Source = ArticleSource.OllamaGenerated;
            existing.SourceDraftId = draft.Id;
            existing.PublishedAt ??= DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = request.PerformedBy;
        }

        db.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = draft.Id,
            EventType = SeoArticleWorkflowEventTypes.PublishedToSite,
            Notes = string.IsNullOrWhiteSpace(request.Notes)
                ? $"Published '{draft.Title}' live to the site blog."
                : request.Notes.Trim(),
            CreatedBy = request.PerformedBy
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Published Content Studio draft {DraftId} live to the site blog at slug {Slug}.",
            draft.Id,
            draft.Slug);

        return await GetDraftAsync(draft.Id, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> RejectDraftAsync(
        DraftDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ApplyDecisionAsync(request, SeoArticleDraftStatuses.Rejected, SeoArticleWorkflowEventTypes.Rejected, cancellationToken);
    }

    public static string BuildPrompt(ArticleGenerationRequest request, IReadOnlyCollection<ScoredAffiliateOfferView>? scoredOffers = null)
    {
        var affiliatePromptBlock = BuildAffiliateOfferPromptContext(scoredOffers ?? Array.Empty<ScoredAffiliateOfferView>());

        return $$"""
        You are a senior technical writer with two master's degrees:
        one in journalism and one in computer science with deep specialization
        in professional software development using C#, .NET, ASP.NET Core, and Blazor.

        You have written over 100 technical articles and books, worked with Fortune 500
        companies, and built enterprise-level software systems.

        Write a professional technical article.

        Requirements:
        - Topic: {{request.Topic}}
        - Target audience: {{request.TargetAudience}}
        - Primary SEO keyword: {{request.PrimaryKeyword}}
        - Secondary keywords: {{request.SecondaryKeywords}}
        - Author voice: clear, practical, authoritative, developer-friendly
        - Format: GitHub-flavored Markdown
        - Include a strong introduction hook
        - Include useful headings
        - Include practical C# examples where appropriate
        - Follow modern C# conventions
        - Avoid filler
        - Keep the article useful for real developers
        - Include a conclusion with next steps
        - Do not invent citations
        - Do not include fake links
        - Keep reading time roughly 8 to 10 minutes
        - If affiliate placeholders are provided, preserve them exactly (do not rename tokens)

        Article structure:
        # Title
        ## Introduction
        ## The Problem
        ## The Practical Solution
        ## C# Implementation
        ## Common Mistakes
        ## Best Practices
        ## Final Thoughts

        {{affiliatePromptBlock}}
        """;
    }

    public static string CreateSlug(string value)
    {
        var allowed = value
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character == '-')
            .ToArray();

        return string.Join(
            '-',
            new string(allowed).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<ArticleGenerationResult?> ApplyDecisionAsync(
        DraftDecisionRequest request,
        string status,
        string workflowEventType,
        CancellationToken cancellationToken)
    {
        var draft = await db.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        draft.Status = status;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        draft.UpdatedBy = request.PerformedBy;
        draft.RequestedModifications = request.Notes.Trim();

        if (status == SeoArticleDraftStatuses.Approved)
        {
            draft.ApprovedAt = DateTimeOffset.UtcNow;
            draft.RejectedAt = null;
        }
        else if (status == SeoArticleDraftStatuses.Rejected)
        {
            draft.RejectedAt = DateTimeOffset.UtcNow;
        }

        db.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = draft.Id,
            EventType = workflowEventType,
            Notes = string.IsNullOrWhiteSpace(request.Notes)
                ? $"Draft marked as {status}."
                : request.Notes.Trim(),
            CreatedBy = request.PerformedBy
        });

        await db.SaveChangesAsync(cancellationToken);

        return await GetDraftAsync(draft.Id, cancellationToken);
    }

    private static string BuildMetaTitle(string topic)
    {
        var cleanTopic = topic.Trim();

        return cleanTopic.Length <= 60
            ? cleanTopic
            : cleanTopic[..60].Trim();
    }

    private static string BuildMetaDescription(ArticleGenerationRequest request)
    {
        var description = $"Learn {request.Topic.Trim()} with practical C# guidance, clean architecture thinking, and real-world .NET development advice.";

        return description.Length <= 155
            ? description
            : description[..155].Trim();
    }

    private static string BuildKeywords(ArticleGenerationRequest request)
    {
        return string.Join(", ",
            new[]
            {
                request.PrimaryKeyword,
                request.SecondaryKeywords,
                "C#",
                ".NET",
                "ASP.NET Core",
                "Blazor"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildDefaultChecklist(string primaryKeyword, string secondaryKeywords) => $"""
        - Primary keyword: {primaryKeyword}
        - Secondary keywords: {secondaryKeywords}
        - Title clearly reflects the core message.
        - Code snippets are short, focused, and formatted as GitHub-flavored Markdown.
        - Code examples follow modern C# naming conventions.
        - Any claims requiring support are checked against Microsoft Learn or official docs before publishing.
        - Article has a hook, body, conclusion, and references section.
        - Estimated reading time stays under 8-10 minutes unless intentionally marked as a deep-dive.
        """;

    private static string ExtractSection(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return string.Empty;
        }

        var next = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.OrdinalIgnoreCase);

        return next < 0
            ? markdown[start..].Trim()
            : markdown[start..next].Trim();
    }

    private static string BuildAffiliateOfferPromptContext(IReadOnlyCollection<ScoredAffiliateOfferView> scoredOffers)
    {
        if (scoredOffers.Count == 0)
        {
            return $"Affiliate placeholders to include exactly as written:\n- {AffiliateSlotTokens[0]}\n- {AffiliateSlotTokens[1]}\n- {AffiliateSlotTokens[2]}";
        }

        var offerLines = scoredOffers
            .Select((offer, index) => $"- Offer {index + 1}: {offer.AdvertiserName} (ID: {offer.AdvertiserId}, Category: {offer.Category}, URL: {offer.TrackingUrl})")
            .ToArray();

        return $"CJ affiliate offers you may reference:\n{string.Join("\n", offerLines)}\n\nAffiliate placeholders to include exactly as written:\n- {AffiliateSlotTokens[0]}\n- {AffiliateSlotTokens[1]}\n- {AffiliateSlotTokens[2]}";
    }

    private static string EnsureAffiliatePlaceholders(string markdown)
    {
        var result = markdown.Trim();

        foreach (var token in AffiliateSlotTokens)
        {
            if (!result.Contains(token, StringComparison.Ordinal))
            {
                result += $"\n\n{token}";
            }
        }

        return result;
    }

    private static IReadOnlyList<SeoArticleAffiliatePlacement> BuildAffiliatePlacements(
        Guid draftId,
        IReadOnlyList<ScoredAffiliateOfferView> scoredOffers)
    {
        var placements = new List<SeoArticleAffiliatePlacement>();

        for (var i = 0; i < AffiliateSlotTokens.Length && i < scoredOffers.Count; i += 1)
        {
            var offer = scoredOffers[i];

            placements.Add(new SeoArticleAffiliatePlacement
            {
                SeoArticleDraftId = draftId,
                SlotToken = AffiliateSlotTokens[i],
                AdvertiserId = offer.AdvertiserId,
                AdvertiserName = offer.AdvertiserName,
                Category = offer.Category,
                TrackingUrl = offer.TrackingUrl,
                CallToActionText = "Explore Offer",
                SortOrder = i + 1,
                CreatedBy = "content-studio"
            });
        }

        return placements;
    }

    private static string RenderAffiliatePlacements(string markdown, IReadOnlyList<ArticleAffiliatePlacementView> placements)
    {
        var rendered = markdown;

        foreach (var placement in placements)
        {
            var card = BuildAffiliateCardMarkdown(placement);
            rendered = rendered.Replace(placement.SlotToken, card, StringComparison.Ordinal);
        }

        foreach (var token in AffiliateSlotTokens)
        {
            rendered = rendered.Replace(token, string.Empty, StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string BuildAffiliateCardMarkdown(ArticleAffiliatePlacementView placement)
    {
        var safeCategory = string.IsNullOrWhiteSpace(placement.Category) ? "General" : placement.Category;
        var safeUrl = string.IsNullOrWhiteSpace(placement.TrackingUrl) ? "#" : placement.TrackingUrl;

        return $"<div class=\"cj-ad-card\"><p><strong>Sponsored Pick: {placement.AdvertiserName}</strong></p><p>Category: {safeCategory}</p><p><a href=\"{safeUrl}\" target=\"_blank\" rel=\"noopener noreferrer nofollow sponsored\">{placement.CallToActionText}</a></p></div>";
    }

    private static ArticleGenerationResult MapDraft(
        SeoArticleDraft draft,
        IReadOnlyDictionary<string, (int Impressions, int Clicks, double CurrentCtr7Day, double PreviousCtr7Day, double Delta7Day)>? metricsBySlot = null,
        string authorName = ContentStudioOptions.DefaultAuthorName,
        string siteBaseUrl = ContentStudioOptions.DefaultSiteBaseUrl,
        IReadOnlyCollection<SeoArticleWorkflowEvent>? workflowEvents = null)
    {
        var events = workflowEvents ?? draft.WorkflowEvents.ToArray();
        var metrics = metricsBySlot ?? new Dictionary<string, (int Impressions, int Clicks, double CurrentCtr7Day, double PreviousCtr7Day, double Delta7Day)>(StringComparer.Ordinal);
        var placements = draft.AffiliatePlacements
            .OrderBy(x => x.SortOrder)
            .Select(x =>
            {
                var interaction = metrics.TryGetValue(x.SlotToken, out var values)
                    ? values
                    : (Impressions: 0, Clicks: 0, CurrentCtr7Day: 0d, PreviousCtr7Day: 0d, Delta7Day: 0d);

                var ctr = CalculateCtr(interaction.Clicks, interaction.Impressions);

                return new ArticleAffiliatePlacementView
                {
                    SlotToken = x.SlotToken,
                    AdvertiserId = x.AdvertiserId,
                    AdvertiserName = x.AdvertiserName,
                    Category = x.Category,
                    TrackingUrl = x.TrackingUrl,
                    CallToActionText = x.CallToActionText,
                    SortOrder = x.SortOrder,
                    Impressions = interaction.Impressions,
                    Clicks = interaction.Clicks,
                    ClickThroughRate = ctr,
                    Current7DayClickThroughRate = interaction.CurrentCtr7Day,
                    Previous7DayClickThroughRate = interaction.PreviousCtr7Day,
                    ClickThroughRateDelta7Day = interaction.Delta7Day
                };
            })
            .ToArray();

        var renderedMarkdown = RenderAffiliatePlacements(draft.ArticleMarkdown, placements);

        return new ArticleGenerationResult
        {
            DraftId = draft.Id,
            Topic = draft.Topic,
            TargetAudience = draft.TargetAudience,
            PrimaryKeyword = draft.PrimaryKeyword,
            SecondaryKeywords = draft.SecondaryKeywords,
            Title = draft.Title,
            Slug = draft.Slug,
            Author = authorName,
            Markdown = draft.ArticleMarkdown,
            RenderedMarkdown = renderedMarkdown,
            PublishMarkdown = renderedMarkdown,
            MetaTitle = BuildMetaTitle(draft.Title),
            MetaDescription = draft.MetaDescription,
            Keywords = BuildKeywords(new ArticleGenerationRequest
            {
                Topic = draft.Topic,
                TargetAudience = draft.TargetAudience,
                PrimaryKeyword = draft.PrimaryKeyword,
                SecondaryKeywords = draft.SecondaryKeywords
            }),
            CanonicalUrl = $"{siteBaseUrl}/blog/{draft.Slug}",
            Status = draft.Status,
            RequestedModifications = draft.RequestedModifications,
            RevisionNumber = draft.RevisionNumber,
            CreatedAt = draft.CreatedAt,
            UpdatedAt = draft.UpdatedAt,
            ApprovedAt = draft.ApprovedAt,
            RejectedAt = draft.RejectedAt,
            AffiliatePlacements = placements,
            HeroImage = new ArticleHeroImagePreview
            {
                AltText = draft.HeroImageAltText,
                DataUri = draft.HeroImageDataUri,
                Caption = draft.HeroImageCaption,
            },
            WorkflowHistory = events
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new ContentStudioWorkflowEntry
                {
                    EventType = x.EventType,
                    Notes = x.Notes,
                    PerformedBy = x.CreatedBy,
                    OccurredAt = x.CreatedAt
                })
                .ToArray()
        };
    }

    public async Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        var draft = await db.SeoArticleDrafts
            .Include(x => x.AffiliatePlacements)
            .Include(x => x.WorkflowEvents)
            .FirstOrDefaultAsync(x => x.Id == draftId, cancellationToken);

        if (draft is null) return false;

        var interactions = await db.SeoArticleAffiliateInteractions
            .Where(x => x.SeoArticleDraftId == draftId)
            .ToListAsync(cancellationToken);

        db.SeoArticleAffiliateInteractions.RemoveRange(interactions);
        db.SeoArticleDrafts.Remove(draft);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task RecordDraftImpressionsAsync(IAppDbContext context, Guid draftId, string performedBy, CancellationToken cancellationToken)
    {
        var placements = await context.SeoArticleAffiliatePlacements
            .AsNoTracking()
            .Where(x => x.SeoArticleDraftId == draftId)
            .Select(x => new { x.SeoArticleDraftId, x.SlotToken, x.AdvertiserId })
            .ToListAsync(cancellationToken);

        if (placements.Count == 0)
        {
            return;
        }

        foreach (var placement in placements)
        {
            context.SeoArticleAffiliateInteractions.Add(new SeoArticleAffiliateInteraction
            {
                SeoArticleDraftId = placement.SeoArticleDraftId,
                SlotToken = placement.SlotToken,
                AdvertiserId = placement.AdvertiserId,
                EventType = AffiliateInteractionEventTypes.Impression,
                CreatedBy = performedBy
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static double CalculateCtr(int clicks, int impressions)
    {
        return impressions == 0
            ? 0
            : (double)clicks / impressions;
    }

    private async Task<SeoArticleDraft?> LoadDraftSnapshotAsync(IAppDbContext context, Guid draftId, CancellationToken cancellationToken)
    {
        return await context.SeoArticleDrafts
            .AsNoTracking()
            .Include(x => x.AffiliatePlacements)
            .Include(x => x.WorkflowEvents)
            .FirstOrDefaultAsync(x => x.Id == draftId, cancellationToken);
    }

    private string GetAuthorName()
    {
        var configured = options.Value.AuthorName?.Trim();
        return string.IsNullOrWhiteSpace(configured)
            ? ContentStudioOptions.DefaultAuthorName
            : configured;
    }

    private string GetSiteBaseUrl()
    {
        var configured = options.Value.SiteBaseUrl?.Trim();
        return string.IsNullOrWhiteSpace(configured)
            ? ContentStudioOptions.DefaultSiteBaseUrl
            : configured.TrimEnd('/');
    }
}
