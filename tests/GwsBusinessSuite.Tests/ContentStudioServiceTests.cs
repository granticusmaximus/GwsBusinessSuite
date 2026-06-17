using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Abstractions;
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
        Assert.Contains("GitHub-flavored Markdown", prompt);
    }

    [Fact]
    public void HeroImagePrompt_ShouldIncludeRequiredSectionsAndNegativeTerms()
    {
        var request = new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        };

        var prompt = ContentStudioImagePreviewFactory.BuildPrompt(request, "Clean Architecture in Blazor Applications");

        var requiredSections = new[]
        {
            "SUBJECT",
            "TYPOGRAPHY RULES",
            "VISUAL STYLE",
            "COMPOSITION",
            "QUALITY REQUIREMENTS",
            "NEGATIVE PROMPT",
            "OUTPUT FORMAT"
        };

        foreach (var section in requiredSections)
        {
            Assert.Contains(section, prompt);
        }

        var requiredNegativeTerms = new[]
        {
            "gibberish text",
            "distorted letters",
            "malformed fonts",
            "unreadable typography",
            "duplicate characters",
            "blurry text",
            "watermark",
            "logo",
            "random symbols"
        };

        foreach (var term in requiredNegativeTerms)
        {
            Assert.Contains(term, prompt);
        }
    }

    [Fact]
    public async Task GenerateArticleAsync_ShouldPersistDraftAndWorkflow_WithRealHeroImagePayload()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Title\n\nGenerated body",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,AAA123",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };

        var service = CreateService(db, ollama);

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
        Assert.Contains("Clean Architecture in Blazor", result.HeroImage.Prompt);
        Assert.Contains("Blazor clean architecture", result.HeroImage.Prompt);
        Assert.True(result.HeroImage.IsGeneratedByOllama);
        Assert.Equal("data:image/png;base64,AAA123", result.HeroImage.DataUri);
        Assert.Contains("x/z-image-turbo", result.HeroImage.StatusMessage);

        var savedDraft = await db.SeoArticleDrafts.SingleAsync();
        Assert.Equal("PendingReview", savedDraft.Status);
        Assert.True(savedDraft.IsHeroImageGeneratedByOllama);
        Assert.Equal(1, await db.SeoArticleWorkflowEvents.CountAsync());
        Assert.Equal("Generated", (await db.SeoArticleWorkflowEvents.SingleAsync()).EventType);
    }

    [Fact]
    public async Task RequestRevisionAndApprove_ShouldPersistWorkflowTransitions()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Initial draft",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,IMG",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };
        var service = CreateService(db, ollama);

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
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Initial draft",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,IMG",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };
        var sanity = new FakeSanityPublisher();
        var service = CreateService(db, ollama, sanity);

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
        Assert.Empty(sanity.PublishedDrafts);
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
    public async Task BackupDraftToSanityAsync_ShouldPublishApprovedDraftAndRecordWorkflowEvent()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Initial draft",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,IMG",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };
        var sanity = new FakeSanityPublisher();
        var service = CreateService(db, ollama, sanity);

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
            Notes = "Ready to back up.",
            PerformedBy = "editor"
        });

        var published = await service.BackupDraftToSanityAsync(new DraftPublishRequest
        {
            DraftId = generated.DraftId,
            Notes = "Back up approved article.",
            PerformedBy = "publisher"
        });

        Assert.NotNull(published);
        Assert.Equal("Approved", published!.Status);
        Assert.Single(sanity.PublishedDrafts);
        Assert.Equal(generated.DraftId, sanity.PublishedDrafts[0].DraftId);
        Assert.Contains("seoArticle", sanity.LastPublishedType);
        Assert.Contains(SeoArticleWorkflowEventTypes.BackedUpToSanity, published.WorkflowHistory.Select(x => x.EventType));
    }

    [Fact]
    public async Task ApproveDraftAsync_ShouldNotBackUpToSanityImplicitly()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Initial draft",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,IMG",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };
        var sanity = new FakeSanityPublisher();
        var service = CreateService(db, ollama, sanity);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        var approved = await service.ApproveDraftAsync(new DraftDecisionRequest
        {
            DraftId = generated.DraftId,
            Notes = "Ready to publish.",
            PerformedBy = "editor"
        });

        Assert.NotNull(approved);
        Assert.Equal("Approved", approved!.Status);
        Assert.Empty(sanity.PublishedDrafts);
        Assert.DoesNotContain(SeoArticleWorkflowEventTypes.BackedUpToSanity, approved.WorkflowHistory.Select(x => x.EventType));
    }

    [Fact]
    public async Task RegenerateHeroImageAsync_ShouldRefreshImageWithoutChangingMarkdown()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Initial draft",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,INITIAL",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };
        var service = CreateService(db, ollama);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        ollama.GenerateImageResult = new OllamaImageGenerationResult(
            DataUri: "data:image/png;base64,REGENERATED",
            MimeType: "image/png",
            Model: "x/z-image-turbo");

        var updated = await service.RegenerateHeroImageAsync(new DraftHeroImageRegenerationRequest
        {
            DraftId = generated.DraftId,
            Notes = "Retry with current local model.",
            PerformedBy = "editor"
        });

        Assert.NotNull(updated);
        Assert.Contains("# Initial draft", updated!.Markdown);
        Assert.Equal("data:image/png;base64,REGENERATED", updated.HeroImage.DataUri);
        Assert.Contains("HeroImageRegenerated", updated.WorkflowHistory.Select(x => x.EventType));
    }

    [Fact]
    public async Task RegenerateHeroImageAsync_ShouldUseCustomPrompt_WhenProvided()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Initial draft",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,CUSTOM_PROMPT_IMAGE",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo" }
        };
        var service = CreateService(db, ollama);

        var generated = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        const string customPrompt = "Create a minimalist server-rack illustration with blue accents and no text.";

        var updated = await service.RegenerateHeroImageAsync(new DraftHeroImageRegenerationRequest
        {
            DraftId = generated.DraftId,
            Prompt = customPrompt,
            Notes = "Use custom visual style prompt.",
            PerformedBy = "editor"
        });

        Assert.NotNull(updated);
        Assert.Equal(customPrompt, updated!.HeroImage.Prompt);
        Assert.Equal(customPrompt, ollama.LastImagePrompt);
    }

    [Fact]
    public async Task GenerateArticleAsync_ShouldFallbackToLatestImageModelTag_WhenConfiguredModelFails()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "# Title\n\nGenerated body",
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,FALLBACK_OK",
                MimeType: "image/png",
                Model: "x/z-image-turbo:latest"),
            Models = new[] { "llama3.2:latest", "x/z-image-turbo:latest" },
            AllowedImageModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "x/z-image-turbo:latest"
            }
        };

        var service = CreateService(db, ollama);

        var result = await service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        Assert.True(result.HeroImage.IsGeneratedByOllama);
        Assert.Equal("data:image/png;base64,FALLBACK_OK", result.HeroImage.DataUri);
        Assert.Contains("x/z-image-turbo:latest", result.HeroImage.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateArticleAsync_ShouldThrowWhenOllamaReturnsEmptyDraft()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService
        {
            GenerateTextResult = string.Empty,
            GenerateImageResult = new OllamaImageGenerationResult(
                DataUri: "data:image/png;base64,IMG",
                MimeType: "image/png",
                Model: "x/z-image-turbo"),
            Models = new[] { "llama3.2:latest" }
        };
        var service = CreateService(db, ollama);

        var action = () => service.GenerateArticleAsync(new ArticleGenerationRequest
        {
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(action);
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static ContentStudioService CreateService(
        ApplicationDbContext db,
        IOllamaService ollama,
        ISanityPublisher? sanityPublisher = null)
    {
        var options = Options.Create(new ContentStudioOptions
        {
            Model = "llama3.2",
            ImageModel = "x/z-image-turbo",
            ImageWidth = 1200,
            ImageHeight = 630,
            ImageSteps = 4
        });

        return new ContentStudioService(
            db,
            ollama,
            new FakeAffiliateOfferScoringService(),
            options,
            sanityPublisher ?? new FakeSanityPublisher(),
            NullLogger<ContentStudioService>.Instance);
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
        public OllamaImageGenerationResult GenerateImageResult { get; set; } = new("", "image/png", "x/z-image-turbo");
        public IReadOnlyCollection<string> Models { get; set; } = Array.Empty<string>();
        public HashSet<string>? AllowedImageModels { get; set; }
        public string LastImagePrompt { get; private set; } = string.Empty;

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            return Task.FromResult(GenerateTextResult);
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(Models);
        }

        public Task<OllamaImageGenerationResult> GenerateImageAsync(OllamaImageGenerationRequest request, CancellationToken ct = default)
        {
            LastImagePrompt = request.Prompt;

            if (AllowedImageModels is not null && AllowedImageModels.Count > 0 && !AllowedImageModels.Contains(request.Model))
            {
                throw new HttpRequestException("Response status code does not indicate success: 500 (Internal Server Error).", null, System.Net.HttpStatusCode.InternalServerError);
            }

            return Task.FromResult(GenerateImageResult with { Model = request.Model });
        }
    }

    private sealed class FakeSanityPublisher : ISanityPublisher
    {
        public List<ArticleGenerationResult> PublishedDrafts { get; } = new();
        public string LastPublishedType { get; private set; } = string.Empty;

        public Task<SanityPublishResult> PublishDraftAsync(ArticleGenerationResult draft, CancellationToken ct = default)
        {
            PublishedDrafts.Add(draft);
            LastPublishedType = "seoArticle";

            return Task.FromResult(new SanityPublishResult(
                true,
                $"Backed up '{draft.Title}' to Sanity.",
                $"gws-seo-{draft.DraftId:N}",
                "tx-test",
                "https://example.sanity.studio/desk/test"));
        }
    }
}
