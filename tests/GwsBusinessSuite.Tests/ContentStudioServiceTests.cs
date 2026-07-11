using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class ContentStudioServiceTests
{
    [Fact]
    public void CreateSlug_ShouldCreateCleanLowercaseSlug()
    {
        var slug = ContentStudioService.CreateSlug("Clean Architecture in Blazor Applications!");

        Assert.Equal("clean-architecture-in-blazor-applications", slug);
    }

    [Fact]
    public void BuildPrompt_ShouldIncludeTopicAndSeoKeyword()
    {
        var request = new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        };

        var prompt = ContentStudioService.BuildPrompt(request);

        Assert.Contains("Clean Architecture in Blazor", prompt);
        Assert.Contains("Blazor clean architecture", prompt);
        Assert.Contains("{{CJ_AD_SLOT_1}}", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ShouldIncludeAccuracyLengthAndStyleRules()
    {
        var systemPrompt = ContentStudioService.BuildSystemPrompt();

        Assert.Contains("1,500 to 2,500 words", systemPrompt);
        Assert.Contains("[VERIFY:", systemPrompt);
        Assert.Contains("GitHub-flavored Markdown", systemPrompt);
        Assert.DoesNotContain('—', systemPrompt);
    }

    [Fact]
    public async Task GenerateArticleAsync_ShouldPersistDraftAndWorkflow()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Title\n\nGenerated body" };
        var service = CreateService(db, factory, ollama);

        var result = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        Assert.Contains("# Title", result.Markdown);
        Assert.Contains("{{CJ_AD_SLOT_1}}", result.Markdown);
        Assert.Contains("Generated body", result.RenderedMarkdown);
        Assert.Equal("clean-architecture-in-blazor", result.Slug);
        Assert.Equal("GWS Editorial", result.Author);

        var savedDraft = await db.SeoArticleDrafts.SingleAsync();
        Assert.Equal("PendingReview", savedDraft.Status);
        Assert.Equal(1, await db.SeoArticleWorkflowEvents.CountAsync());
        Assert.Equal("Generated", (await db.SeoArticleWorkflowEvents.SingleAsync()).EventType);
        Assert.Equal("llama3.2", ollama.LastRequestedModel);
    }

    [Fact]
    public async Task GenerateArticleAsync_ShouldUseSiteSettingsModelOverride_WhenSet()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Title" };
        var service = CreateService(db, factory, ollama);

        await new SiteSettingsService(db).SaveSettingsAsync(
            new SiteSettingsView(10, null, null, "mistral", null, null, 8));

        await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        // The site-configured override takes precedence over the appsettings.json
        // default ("llama3.2") baked into CreateService's ContentStudioOptions.
        Assert.Equal("mistral", ollama.LastRequestedModel);
    }

    [Fact]
    public async Task RequestRevisionAndApprove_ShouldPersistWorkflowTransitions()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Initial draft" };
        var service = CreateService(db, factory, ollama);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        ollama.GenerateTextResult = "# Revised draft";

        var revised = await service.RequestRevisionAsync(new DraftRevisionRequest
        {
            DraftId = generated.DraftId,
            RequestedModifications = "Make the C# sample more enterprise-oriented.",
            PerformedBy = "reviewer"
        });

        Assert.NotNull(revised);
        Assert.Equal(1, revised!.RevisionNumber);
        Assert.Equal("PendingReview", revised.Status);
        Assert.Contains("# Revised draft", revised.Markdown);

        var approved = await service.ApproveDraftAsync(new DraftDecisionRequest
        {
            DraftId = generated.DraftId,
            Notes = "Ready to publish.",
            PerformedBy = "editor"
        });

        Assert.NotNull(approved);
        Assert.Equal("Approved", approved!.Status);
        Assert.NotNull(approved.ApprovedAt);
        Assert.Equal(3, approved.WorkflowHistory.Count);
    }

    [Fact]
    public async Task PublishDraftToSiteAsync_ShouldPublishApprovedDraftLiveAndPersistArticle()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Initial draft" };
        var service = CreateService(db, factory, ollama);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        await service.ApproveDraftAsync(new DraftDecisionRequest
        {
            DraftId = generated.DraftId,
            Notes = "Ready to publish.",
            PerformedBy = "editor"
        });

        var published = await service.PublishDraftToSiteAsync(new DraftPublishRequest
        {
            DraftId = generated.DraftId,
            Notes = "Publish article live.",
            PerformedBy = "publisher"
        });

        Assert.NotNull(published);
        Assert.Equal("Approved", published!.Status);
        Assert.Contains(SeoArticleWorkflowEventTypes.PublishedToSite, published.WorkflowHistory.Select(x => x.EventType));

        var article = await db.Articles.SingleAsync();
        Assert.Equal(generated.Slug, article.Slug);
        Assert.Equal(ArticleStatuses.Published, article.Status);
        Assert.Equal(ArticleSource.OllamaGenerated, article.Source);
        Assert.Equal(generated.DraftId, article.SourceDraftId);
        Assert.Equal("Grant Watson", article.Author);
        Assert.NotNull(article.PublishedAt);
    }

    [Fact]
    public async Task PublishDraftToSiteAsync_ShouldCarryCategoryAndTagsThroughToTheArticle()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Initial draft" };
        var service = CreateService(db, factory, ollama);

        var category = new ArticleCategory { Name = "Dev Tools", Slug = "dev-tools" };
        db.ArticleCategories.Add(category);
        await db.SaveChangesAsync();

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        // GenerateArticleAsync doesn't accept Category/Tags (they're set on the draft
        // afterward, e.g. via the admin UI) - mirror that by updating the draft row directly.
        var draft = await db.SeoArticleDrafts.SingleAsync();
        draft.CategoryId = category.Id;
        draft.Tags = "dotnet, blazor, tutorial";
        await db.SaveChangesAsync();

        await service.ApproveDraftAsync(new DraftDecisionRequest
        {
            DraftId = generated.DraftId,
            Notes = "Ready to publish.",
            PerformedBy = "editor"
        });

        await service.PublishDraftToSiteAsync(new DraftPublishRequest
        {
            DraftId = generated.DraftId,
            Notes = "Publish article live.",
            PerformedBy = "publisher"
        });

        var article = await db.Articles.SingleAsync();
        Assert.Equal(category.Id, article.CategoryId);
        Assert.Equal("dotnet, blazor, tutorial", article.Tags);

        // Republishing (update-existing-by-slug path) should carry the same fields too.
        draft.Status = SeoArticleDraftStatuses.PendingReview;
        await db.SaveChangesAsync();
        await service.ApproveDraftAsync(new DraftDecisionRequest
        {
            DraftId = generated.DraftId,
            Notes = "Re-approve.",
            PerformedBy = "editor"
        });
        draft.Tags = "dotnet, blazor, updated";
        await db.SaveChangesAsync();
        await service.PublishDraftToSiteAsync(new DraftPublishRequest
        {
            DraftId = generated.DraftId,
            Notes = "Republish.",
            PerformedBy = "publisher"
        });

        var republished = await db.Articles.SingleAsync();
        Assert.Equal(category.Id, republished.CategoryId);
        Assert.Equal("dotnet, blazor, updated", republished.Tags);
    }

    [Fact]
    public async Task DeletingAnArticleCategory_ShouldSetArticlesToUncategorizedRatherThanBlockOrCascade()
    {
        var (db, factory) = await CreateDbAsync();

        var category = new ArticleCategory { Name = "Dev Tools", Slug = "dev-tools" };
        db.ArticleCategories.Add(category);
        var article = new Article
        {
            Slug = "test-article",
            Title = "Test Article",
            CategoryId = category.Id,
            CreatedBy = "test"
        };
        db.Articles.Add(article);
        await db.SaveChangesAsync();

        db.ArticleCategories.Remove(category);
        await db.SaveChangesAsync();

        var reloaded = await db.Articles.SingleAsync();
        Assert.Null(reloaded.CategoryId);
    }

    [Fact]
    public async Task UpdateDraftMarkdownAsync_ShouldPersistEditedMarkdownWithoutCallingOllama()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Initial draft" };
        var service = CreateService(db, factory, ollama);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        ollama.GenerateTextResult = "# This should never be used";

        var updated = await service.UpdateDraftMarkdownAsync(new DraftMarkdownUpdateRequest
        {
            DraftId = generated.DraftId,
            Markdown = "# Hand-edited title\n\nHand-edited body.",
            PerformedBy = "editor"
        });

        Assert.NotNull(updated);
        Assert.Contains("Hand-edited body.", updated!.Markdown);
        Assert.DoesNotContain("This should never be used", updated.Markdown);
        Assert.Contains(SeoArticleWorkflowEventTypes.ManuallyEdited, updated.WorkflowHistory.Select(x => x.EventType));
    }

    [Fact]
    public async Task UpdateDraftMarkdownAsync_ShouldThrowOnEmptyMarkdown()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = "# Initial draft" };
        var service = CreateService(db, factory, ollama);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        var action = () => service.UpdateDraftMarkdownAsync(new DraftMarkdownUpdateRequest
        {
            DraftId = generated.DraftId,
            Markdown = "   ",
            PerformedBy = "editor"
        });

        await Assert.ThrowsAsync<ArgumentException>(action);
    }

    [Fact]
    public async Task GenerateArticleAsync_ShouldThrowWhenOllamaReturnsEmptyDraft()
    {
        var (db, factory) = await CreateDbAsync();
        var ollama = new FakeOllamaService { GenerateTextResult = string.Empty };
        var service = CreateService(db, factory, ollama);

        var action = () => service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(action);
    }

    private static async Task<(ApplicationDbContext Db, IAppDbContextFactory Factory)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // GenerateArticleAsync/RequestRevisionAsync persist through a freshly created
        // context (mirroring production's dbContextFactory pattern for long-running
        // Ollama calls), so the fake factory must reuse the same open connection rather
        // than creating an unrelated in-memory database.
        return (db, new FakeAppDbContextFactory(options));
    }

    private static ContentStudioService CreateService(
        ApplicationDbContext db,
        IAppDbContextFactory factory,
        IOllamaService ollama)
    {
        var options = Options.Create(new ContentStudioOptions { Model = "llama3.2" });

        return new ContentStudioService(
            db,
            factory,
            ollama,
            new FakeAffiliateOfferScoringService(),
            new AffiliateSuggestionService(db, ollama, options, NullLogger<AffiliateSuggestionService>.Instance),
            new SiteSettingsService(db),
            options,
            NullLogger<ContentStudioService>.Instance);
    }

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class FakeAffiliateOfferScoringService : IAffiliateOfferScoringService
    {
        public Task<IReadOnlyList<ScoredAffiliateOfferView>> ScoreOffersAsync(
            ArticleGenerationRequest request,
            int maxOffers,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ScoredAffiliateOfferView>>(Array.Empty<ScoredAffiliateOfferView>());
        }
    }

    private sealed class FakeOllamaService : IOllamaService
    {
        public string GenerateTextResult { get; set; } = string.Empty;
        public IReadOnlyCollection<string> Models { get; set; } = Array.Empty<string>();
        public string? LastRequestedModel { get; private set; }
        public string GenerateImageResult { get; set; } = string.Empty;
        public string? LastImagePrompt { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastRequestedModel = model;
            return Task.FromResult(GenerateTextResult);
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(Models);
        }

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default)
        {
            LastRequestedModel = model;
            LastImagePrompt = prompt;
            return Task.FromResult(GenerateImageResult);
        }
    }
}