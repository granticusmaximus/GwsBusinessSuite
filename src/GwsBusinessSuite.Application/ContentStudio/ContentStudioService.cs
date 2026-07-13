using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.Settings;
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
    IAffiliateSuggestionService affiliateSuggestionService,
    ISiteSettingsService siteSettingsService,
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

        // Pending-review drafts sort first regardless of age, so one never silently falls
        // off the end of this capped list behind a run of newer approved/rejected drafts.
        return drafts
            .OrderBy(x => x.Status == SeoArticleDraftStatuses.PendingReview ? 0 : 1)
            .ThenByDescending(x => x.CreatedAt)
            .Take(20)
            .ToList();
    }

    public Task<int> CountPendingReviewAsync(CancellationToken cancellationToken = default) =>
        db.SeoArticleDrafts.CountAsync(x => x.Status == SeoArticleDraftStatuses.PendingReview, cancellationToken);

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
        var model = await GetEffectiveModelAsync(cancellationToken);
        var timeout = await GetEffectiveTimeoutAsync(cancellationToken);

        logger.LogInformation(
            "Generating Content Studio draft for topic '{Topic}' using Ollama model '{Model}'.",
            request.Topic,
            model);

        // Ollama's first-time model load plus generation can take several minutes.
        // A Blazor Server circuit can disconnect and have its DI scope (and the
        // constructor-injected db context) disposed during that wait, so the save
        // below uses a freshly created context instead of the field-level `db`.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var markdown = (await ollama.GenerateAsync(model, BuildSystemPrompt(), prompt, timeoutCts.Token)).Trim();

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
        AddRevisionSnapshot(
            freshContext,
            draft,
            versionNumber: 0,
            changeType: SeoArticleWorkflowEventTypes.Generated,
            notes: "Initial generated draft.",
            performedBy: "content-studio");

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

        var configuredModel = await GetEffectiveModelAsync(cancellationToken);
        var timeout = await GetEffectiveTimeoutAsync(cancellationToken);

        // Ollama generation can take several minutes; a Blazor Server circuit can
        // disconnect and dispose the field-level db context during that wait, so the
        // save below re-loads the draft on a freshly created context instead of
        // reusing the entity tracked by the original context.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var revisedMarkdown = (await ollama.GenerateAsync(configuredModel, BuildSystemPrompt(), prompt, timeoutCts.Token)).Trim();
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

        var nextVersionNumber = await PrepareNextRevisionNumberAsync(freshContext, freshDraft, cancellationToken);

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
        AddRevisionSnapshot(
            freshContext,
            freshDraft,
            nextVersionNumber,
            SeoArticleWorkflowEventTypes.Revised,
            revisionNotes,
            request.PerformedBy);

        await freshContext.SaveChangesAsync(cancellationToken);

        return await GetDraftCoreAsync(freshContext, freshDraft.Id, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> GenerateHeroImageAsync(
        DraftHeroImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var draft = await db.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? draft.Title : request.Prompt.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A prompt (or a draft title to fall back to) is required to generate a hero image.", nameof(request));
        }

        var configuredModel = await GetEffectiveImageModelAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            throw new InvalidOperationException("No hero image model is configured. Set one in Settings > AI (Ollama).");
        }

        var timeout = await GetEffectiveTimeoutAsync(cancellationToken);

        // Same reasoning as RequestRevisionAsync above: generation can take a while, so
        // reload on a freshly created context rather than reusing the one the Blazor
        // Server circuit may have disconnected during the wait.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var imageBase64 = await ollama.GenerateImageAsync(configuredModel, prompt, timeoutCts.Token);
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            throw new InvalidOperationException("Ollama returned no image data.");
        }

        await using var freshContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var freshDraft = await freshContext.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == draft.Id, cancellationToken);
        if (freshDraft is null)
        {
            return null;
        }

        freshDraft.HeroImageDataUri = $"data:image/png;base64,{imageBase64}";
        freshDraft.HeroImagePrompt = prompt;
        freshDraft.HeroImageAltText = freshDraft.Title;
        freshDraft.HeroImageProvider = "Ollama";
        freshDraft.HeroImageConfiguredModel = configuredModel;
        freshDraft.IsHeroImageGeneratedByOllama = true;
        freshDraft.UpdatedAt = DateTimeOffset.UtcNow;
        freshDraft.UpdatedBy = request.PerformedBy;

        freshContext.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = freshDraft.Id,
            EventType = SeoArticleWorkflowEventTypes.HeroImageRegenerated,
            Notes = $"Generated via Ollama ({configuredModel}). Prompt: {prompt}",
            CreatedBy = request.PerformedBy
        });

        await freshContext.SaveChangesAsync(cancellationToken);

        return await GetDraftCoreAsync(freshContext, freshDraft.Id, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> UploadHeroImageAsync(
        DraftHeroImageUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DataUri) ||
            !request.DataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid image data URI is required.", nameof(request));
        }

        await using var freshContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var draft = await freshContext.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        draft.HeroImageDataUri = request.DataUri;
        draft.HeroImageAltText = draft.Title;
        draft.HeroImagePrompt = string.Empty;
        draft.HeroImageProvider = "ManualUpload";
        draft.HeroImageConfiguredModel = string.Empty;
        draft.HeroImageAvailableModelsSummary = string.Empty;
        draft.HeroImageStatusMessage = string.Empty;
        draft.IsHeroImageGeneratedByOllama = false;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        draft.UpdatedBy = request.PerformedBy;

        freshContext.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = draft.Id,
            EventType = SeoArticleWorkflowEventTypes.HeroImageRegenerated,
            Notes = "Hero image uploaded manually.",
            CreatedBy = request.PerformedBy
        });

        await freshContext.SaveChangesAsync(cancellationToken);
        return await GetDraftCoreAsync(freshContext, draft.Id, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> UpdateDraftMarkdownAsync(
        DraftMarkdownUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var markdown = request.Markdown.Trim();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new ArgumentException("Markdown cannot be empty.", nameof(request));
        }

        var draft = await db.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var nextVersionNumber = await PrepareNextRevisionNumberAsync(db, draft, cancellationToken);

        draft.ArticleMarkdown = markdown;
        draft.OutlineMarkdown = ExtractSection(markdown, "## Outline");
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        draft.UpdatedBy = request.PerformedBy;

        db.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = draft.Id,
            EventType = SeoArticleWorkflowEventTypes.ManuallyEdited,
            Notes = "Markdown edited directly in the draft workspace.",
            CreatedBy = request.PerformedBy
        });
        AddRevisionSnapshot(
            db,
            draft,
            nextVersionNumber,
            SeoArticleWorkflowEventTypes.ManuallyEdited,
            "Markdown edited directly in the draft workspace.",
            request.PerformedBy);

        await db.SaveChangesAsync(cancellationToken);

        return await GetDraftAsync(draft.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<ContentStudioRevisionView>> GetRevisionHistoryAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        return await db.SeoArticleDraftRevisions
            .AsNoTracking()
            .Where(x => x.SeoArticleDraftId == draftId)
            .OrderByDescending(x => x.VersionNumber)
            .Select(x => new ContentStudioRevisionView
            {
                RevisionId = x.Id,
                VersionNumber = x.VersionNumber,
                ChangeType = x.ChangeType,
                Notes = x.Notes,
                PerformedBy = x.CreatedBy,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<ContentStudioRevisionDiff?> GetRevisionDiffAsync(
        Guid draftId,
        Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        var revision = await db.SeoArticleDraftRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == revisionId && x.SeoArticleDraftId == draftId, cancellationToken);
        if (revision is null)
        {
            return null;
        }

        var previous = await db.SeoArticleDraftRevisions
            .AsNoTracking()
            .Where(x => x.SeoArticleDraftId == draftId && x.VersionNumber < revision.VersionNumber)
            .OrderByDescending(x => x.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

        return new ContentStudioRevisionDiff
        {
            VersionNumber = revision.VersionNumber,
            PreviousVersionNumber = previous?.VersionNumber,
            Lines = BuildLineDiff(previous?.ArticleMarkdown ?? string.Empty, revision.ArticleMarkdown)
        };
    }

    public async Task<ArticleGenerationResult?> RestoreRevisionAsync(
        DraftRevisionRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var draft = await db.SeoArticleDrafts.FirstOrDefaultAsync(x => x.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var revision = await db.SeoArticleDraftRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == request.RevisionId && x.SeoArticleDraftId == request.DraftId,
                cancellationToken);
        if (revision is null)
        {
            throw new InvalidOperationException("The selected draft revision no longer exists.");
        }

        var nextVersionNumber = await PrepareNextRevisionNumberAsync(db, draft, cancellationToken);
        draft.ArticleMarkdown = revision.ArticleMarkdown;
        draft.OutlineMarkdown = revision.OutlineMarkdown;
        draft.Status = SeoArticleDraftStatuses.PendingReview;
        draft.RevisionNumber += 1;
        draft.ApprovedAt = null;
        draft.RejectedAt = null;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        draft.UpdatedBy = request.PerformedBy;

        var notes = $"Restored content from version {revision.VersionNumber}.";
        db.SeoArticleWorkflowEvents.Add(new SeoArticleWorkflowEvent
        {
            SeoArticleDraftId = draft.Id,
            EventType = SeoArticleWorkflowEventTypes.RevisionRestored,
            Notes = notes,
            CreatedBy = request.PerformedBy
        });
        AddRevisionSnapshot(
            db,
            draft,
            nextVersionNumber,
            SeoArticleWorkflowEventTypes.RevisionRestored,
            notes,
            request.PerformedBy);

        await db.SaveChangesAsync(cancellationToken);
        return await GetDraftAsync(draft.Id, cancellationToken);
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

        var draftPlacements = await db.SeoArticleAffiliatePlacements
            .AsNoTracking()
            .Where(x => x.SeoArticleDraftId == draft.Id)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        // Keep the raw markdown (with unresolved {{CJ_AD_*}} tokens) on the live Article
        // rather than baking resolved ad HTML in here. Tokens are resolved dynamically at
        // serve time from ArticleAffiliatePlacement rows, so ads can be edited, moved, or
        // added on the published article afterward without needing to republish.
        var existing = await db.Articles
            .FirstOrDefaultAsync(article => article.Slug == draft.Slug, cancellationToken);

        Article article;
        var isNewArticle = existing is null;

        if (existing is null)
        {
            article = new Article
            {
                Slug = draft.Slug,
                Title = draft.Title,
                Topic = draft.Topic,
                BodyMarkdown = draft.ArticleMarkdown,
                MetaDescription = draft.MetaDescription,
                PrimaryKeyword = draft.PrimaryKeyword,
                SecondaryKeywords = draft.SecondaryKeywords,
                CategoryId = draft.CategoryId,
                Tags = draft.Tags,
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
            };
            db.Articles.Add(article);
        }
        else
        {
            article = existing;
            existing.Title = draft.Title;
            existing.Topic = draft.Topic;
            existing.BodyMarkdown = draft.ArticleMarkdown;
            existing.MetaDescription = draft.MetaDescription;
            existing.PrimaryKeyword = draft.PrimaryKeyword;
            existing.SecondaryKeywords = draft.SecondaryKeywords;
            existing.CategoryId = draft.CategoryId;
            existing.Tags = draft.Tags;
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

        // Only seed ad placements on the article's first publish. Once live, ads are
        // managed independently via the Article Editor and shouldn't be overwritten by
        // republishing the source draft.
        if (isNewArticle)
        {
            foreach (var draftPlacement in draftPlacements)
            {
                db.ArticleAffiliatePlacements.Add(new ArticleAffiliatePlacement
                {
                    ArticleId = article.Id,
                    SlotToken = draftPlacement.SlotToken,
                    AdvertiserId = draftPlacement.AdvertiserId,
                    AdvertiserName = draftPlacement.AdvertiserName,
                    Category = draftPlacement.Category,
                    TrackingUrl = draftPlacement.TrackingUrl,
                    CallToActionText = draftPlacement.CallToActionText,
                    SortOrder = draftPlacement.SortOrder,
                    CreatedBy = request.PerformedBy
                });
            }
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

        // Best-effort: only worth suggesting on the article's first publish (isNewArticle) -
        // a republish of an already-live article shouldn't churn its existing suggestions/ad
        // placements. A failed AI match here shouldn't undo a successful publish.
        if (isNewArticle)
        {
            try
            {
                await affiliateSuggestionService.GenerateForArticleAsync(article.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Affiliate suggestion generation failed for newly published article {Id}.", article.Id);
            }
        }

        return await GetDraftAsync(draft.Id, cancellationToken);
    }

    public async Task<ArticleGenerationResult?> RejectDraftAsync(
        DraftDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ApplyDecisionAsync(request, SeoArticleDraftStatuses.Rejected, SeoArticleWorkflowEventTypes.Rejected, cancellationToken);
    }

    public static string BuildSystemPrompt() => """
        You are a senior software engineer and technical writer producing reference-quality
        how-to guides for working developers. Your readers are fluent professionals: do not
        over-explain fundamentals. The bar is that a colleague would read this, trust it, and
        share it as a reference.

        Length:
        - Target 1,500 to 2,500 words. This is a budget, not a quota: cut filler, restated
          points, and motivational padding. A tight 1,500-word guide beats a padded 2,400-word
          one.

        Accuracy and integrity (highest priority):
        - Never invent APIs, method names, signatures, parameters, CLI flags, config keys,
          library names, version numbers, or benchmark figures. If a name or signature is
          uncertain, insert a clearly marked [VERIFY: ...] placeholder instead of guessing.
        - Prefer standard-library and widely adopted, stable APIs over obscure ones.
        - Separate documented fact from opinion. Label recommendations and tradeoffs as such.
        - Do not fabricate sources, quotes, or statistics. If a claim needs a citation, name
          the official source (for example the language or framework docs) without inventing
          URLs.

        Code examples:
        - Every code block must be syntactically valid and runnable as shown, or explicitly
          note what is omitted (imports, setup, package install).
        - One concept per example. Minimal but realistic: real naming, relevant error
          handling, comments only on non-obvious lines.
        - Keep lines roughly under 80 characters so they do not horizontally scroll on mobile.
        - When showing a "before vs after" or "wrong vs right" contrast, the two versions must
          actually differ in behavior or structure. Do not present two near-identical snippets
          as a contrast.
        - State language and version assumptions once, up front.

        Structure:
        - Title in sentence case.
        - Short intro: the problem, who it is for, and what the reader will have built or
          learned by the end.
        - Prerequisites (versions, tools, prior knowledge).
        - Clearly headed or numbered steps, with code paired to each step.
        - A "common pitfalls" or "edge cases and tradeoffs" section.
        - Brief recap. No filler conclusion.

        Style:
        - Clear, direct, senior-engineer voice. Plain language, technically precise.
        - Sentence case headings. No hype adjectives (pristine, seamless, blazing fast, game
          changing).
        - Do not use em-dashes. Use commas, colons, or parentheses instead.
        - Skimmable: short paragraphs, descriptive headings, code carrying the teaching load.

        Before returning the article, self-check:
        - Is every API and signature real, or marked [VERIFY]?
        - Does every code block run as shown, or note its omissions?
        - Is the length within 1,500 to 2,500 words without padding?
        - Is each non-obvious claim either common knowledge or attributed to a named source?

        Output format: GitHub-flavored Markdown. Do not include fake links.
        """;

    public static string BuildPrompt(ArticleGenerationRequest request, IReadOnlyCollection<ScoredAffiliateOfferView>? scoredOffers = null)
    {
        var affiliatePromptBlock = BuildAffiliateOfferPromptContext(scoredOffers ?? Array.Empty<ScoredAffiliateOfferView>());

        return $$"""
        Write the how-to guide described below, following all rules already given.

        - Topic: {{request.Topic}}
        - Target audience: {{request.TargetAudience}}
        - Primary SEO keyword: {{request.PrimaryKeyword}}
        - Secondary keywords: {{request.SecondaryKeywords}}
        - Primary language/framework for code examples: C#, .NET, ASP.NET Core, or Blazor as the topic requires
        - If affiliate placeholders are provided below, preserve them exactly (do not rename tokens)

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
        var markup = placements
            .Select(placement => new ArticleMarkdownRenderer.AffiliatePlacementMarkup(
                placement.SlotToken,
                placement.AdvertiserName,
                placement.Category,
                placement.TrackingUrl,
                placement.CallToActionText))
            .ToArray();

        return ArticleMarkdownRenderer.Render(markdown, markup);
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
                Prompt = draft.HeroImagePrompt,
                IsGeneratedByOllama = draft.IsHeroImageGeneratedByOllama,
                ConfiguredModel = draft.HeroImageConfiguredModel,
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

    private static void AddRevisionSnapshot(
        IAppDbContext context,
        SeoArticleDraft draft,
        int versionNumber,
        string changeType,
        string notes,
        string performedBy)
    {
        context.SeoArticleDraftRevisions.Add(new SeoArticleDraftRevision
        {
            SeoArticleDraftId = draft.Id,
            VersionNumber = versionNumber,
            ArticleMarkdown = draft.ArticleMarkdown,
            OutlineMarkdown = draft.OutlineMarkdown,
            ChangeType = changeType,
            Notes = notes,
            CreatedBy = performedBy
        });
    }

    private static async Task<int> PrepareNextRevisionNumberAsync(
        IAppDbContext context,
        SeoArticleDraft draft,
        CancellationToken cancellationToken)
    {
        var latestVersion = await context.SeoArticleDraftRevisions
            .Where(x => x.SeoArticleDraftId == draft.Id)
            .Select(x => (int?)x.VersionNumber)
            .MaxAsync(cancellationToken);

        if (latestVersion is not null)
        {
            return latestVersion.Value + 1;
        }

        context.SeoArticleDraftRevisions.Add(new SeoArticleDraftRevision
        {
            SeoArticleDraftId = draft.Id,
            VersionNumber = 0,
            ArticleMarkdown = draft.ArticleMarkdown,
            OutlineMarkdown = draft.OutlineMarkdown,
            ChangeType = "Baseline",
            Notes = "Baseline captured before the first versioned edit.",
            CreatedAt = draft.UpdatedAt ?? draft.CreatedAt,
            CreatedBy = draft.UpdatedBy ?? draft.CreatedBy
        });
        return 1;
    }

    internal static IReadOnlyList<ContentStudioDiffLine> BuildLineDiff(string before, string after)
    {
        var beforeLines = before.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var afterLines = after.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lengths = new int[beforeLines.Length + 1, afterLines.Length + 1];

        for (var beforeIndex = beforeLines.Length - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterLines.Length - 1; afterIndex >= 0; afterIndex--)
            {
                lengths[beforeIndex, afterIndex] = string.Equals(beforeLines[beforeIndex], afterLines[afterIndex], StringComparison.Ordinal)
                    ? lengths[beforeIndex + 1, afterIndex + 1] + 1
                    : Math.Max(lengths[beforeIndex + 1, afterIndex], lengths[beforeIndex, afterIndex + 1]);
            }
        }

        var result = new List<ContentStudioDiffLine>();
        var i = 0;
        var j = 0;
        while (i < beforeLines.Length && j < afterLines.Length)
        {
            if (string.Equals(beforeLines[i], afterLines[j], StringComparison.Ordinal))
            {
                result.Add(new ContentStudioDiffLine { Kind = "unchanged", Text = beforeLines[i] });
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                result.Add(new ContentStudioDiffLine { Kind = "removed", Text = beforeLines[i++] });
            }
            else
            {
                result.Add(new ContentStudioDiffLine { Kind = "added", Text = afterLines[j++] });
            }
        }

        while (i < beforeLines.Length)
        {
            result.Add(new ContentStudioDiffLine { Kind = "removed", Text = beforeLines[i++] });
        }

        while (j < afterLines.Length)
        {
            result.Add(new ContentStudioDiffLine { Kind = "added", Text = afterLines[j++] });
        }

        return result;
    }

    private async Task<string> GetEffectiveModelAsync(CancellationToken cancellationToken)
    {
        var settings = await siteSettingsService.GetSettingsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.OllamaModelOverride))
        {
            return settings.OllamaModelOverride;
        }

        return string.IsNullOrWhiteSpace(options.Value.Model)
            ? ContentStudioOptions.DefaultModel
            : options.Value.Model;
    }

    // Unlike GetEffectiveModelAsync, there's no safe app-wide default image-generation
    // model to fall back to (an arbitrary text model can't generate images), so an unset
    // override means "not configured" rather than "use the default."
    private async Task<string?> GetEffectiveImageModelAsync(CancellationToken cancellationToken)
    {
        var settings = await siteSettingsService.GetSettingsAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(settings.HeroImageModelOverride) ? null : settings.HeroImageModelOverride;
    }

    private async Task<TimeSpan> GetEffectiveTimeoutAsync(CancellationToken cancellationToken)
    {
        var settings = await siteSettingsService.GetSettingsAsync(cancellationToken);
        var minutes = settings.OllamaTimeoutMinutesOverride
            ?? (options.Value.GenerationTimeoutMinutes <= 0
                ? ContentStudioOptions.DefaultGenerationTimeoutMinutes
                : options.Value.GenerationTimeoutMinutes);

        return TimeSpan.FromMinutes(minutes);
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
