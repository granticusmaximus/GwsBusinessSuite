using FluentAssertions;
using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class AffiliateSuggestionServiceTests
{
    [Fact]
    public async Task GenerateForArticleAsync_ShouldCreateSuggestions_ForOllamaPickedOffers()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Best Standing Desks");
        await CreateOfferAsync(db, "adv-1", "Acme Desks", "Standing Desk Pro");

        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "OFFER_INDEX: 1\nREASON: Directly relevant to standing desks."
        };
        var service = CreateService(db, ollama);

        var result = await service.GenerateForArticleAsync(article.Id);

        result.IsSuccess.Should().BeTrue();
        result.SuggestionsCreated.Should().Be(1);
        var suggestions = db.ArticleAffiliateSuggestions.Where(s => s.ArticleId == article.Id).ToList();
        suggestions.Should().ContainSingle(s => s.AdvertiserName == "Acme Desks" && s.Status == ArticleAffiliateSuggestionStatuses.Pending);
    }

    [Fact]
    public async Task GenerateForArticleAsync_ShouldReturnFailure_WhenNoOffersAvailable()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "No Offers Article");
        var service = CreateService(db, new FakeOllamaService());

        var result = await service.GenerateForArticleAsync(article.Id);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("No affiliate offers");
    }

    [Fact]
    public async Task GenerateForArticleAsync_ShouldReturnFailure_WhenArticleNotFound()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeOllamaService());

        var result = await service.GenerateForArticleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task GenerateForArticleAsync_ShouldExcludeOffers_WithExpiredPromotions()
    {
        // Regression test: LoadCandidateOffersAsync previously filtered PromotionEndsAt
        // in the SQL WHERE clause, which SQLite's EF Core provider can't translate for
        // DateTimeOffset range comparisons and threw at runtime. This exercises the
        // actual query path end-to-end with an expired offer present.
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Article With Expired Offer");
        var expired = await CreateOfferAsync(db, "adv-1", "Expired Advertiser", "Expired Deal");
        expired.PromotionEndsAt = DateTimeOffset.UtcNow.AddDays(-1);
        var active = await CreateOfferAsync(db, "adv-2", "Active Advertiser", "Active Deal");
        active.PromotionEndsAt = DateTimeOffset.UtcNow.AddDays(30);
        await db.SaveChangesAsync();

        var ollama = new FakeOllamaService { GenerateTextResult = "OFFER_INDEX: 1\nREASON: Only option available." };
        var service = CreateService(db, ollama);

        var act = () => service.GenerateForArticleAsync(article.Id);

        await act.Should().NotThrowAsync();
        var result = await act();
        result.IsSuccess.Should().BeTrue();
        var suggestion = db.ArticleAffiliateSuggestions.Single(s => s.ArticleId == article.Id);
        suggestion.AdvertiserName.Should().Be("Active Advertiser");
    }

    [Fact]
    public async Task GenerateForArticleAsync_ShouldRemoveExistingPendingSuggestions_ButKeepAppliedOrDismissed()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Re-generated Article");
        await CreateOfferAsync(db, "adv-1", "Acme Desks", "Standing Desk Pro");

        db.ArticleAffiliateSuggestions.Add(new ArticleAffiliateSuggestion
        {
            ArticleId = article.Id,
            AdvertiserName = "Old Pending",
            Status = ArticleAffiliateSuggestionStatuses.Pending,
            Rank = 1
        });
        db.ArticleAffiliateSuggestions.Add(new ArticleAffiliateSuggestion
        {
            ArticleId = article.Id,
            AdvertiserName = "Already Applied",
            Status = ArticleAffiliateSuggestionStatuses.Applied,
            Rank = 1
        });
        await db.SaveChangesAsync();

        var ollama = new FakeOllamaService { GenerateTextResult = "OFFER_INDEX: 1\nREASON: fits." };
        var service = CreateService(db, ollama);

        await service.GenerateForArticleAsync(article.Id);

        var all = db.ArticleAffiliateSuggestions.Where(s => s.ArticleId == article.Id).ToList();
        all.Should().NotContain(s => s.AdvertiserName == "Old Pending");
        all.Should().ContainSingle(s => s.AdvertiserName == "Already Applied");
        all.Should().ContainSingle(s => s.AdvertiserName == "Acme Desks");
    }

    [Fact]
    public async Task GenerateForArticleAsync_ShouldIgnoreOutOfRangeOrDuplicateOfferIndexes()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Bad Ollama Response");
        await CreateOfferAsync(db, "adv-1", "Acme Desks", "Standing Desk Pro");

        var ollama = new FakeOllamaService
        {
            GenerateTextResult = "OFFER_INDEX: 99\nREASON: hallucinated\n---\nOFFER_INDEX: 1\nREASON: real\n---\nOFFER_INDEX: 1\nREASON: duplicate"
        };
        var service = CreateService(db, ollama);

        var result = await service.GenerateForArticleAsync(article.Id);

        result.SuggestionsCreated.Should().Be(1);
    }

    [Fact]
    public async Task GenerateForAllArticlesAsync_ShouldProcessEveryNonTrashedArticle()
    {
        await using var db = await CreateDbAsync();
        var article1 = await CreateArticleAsync(db, "Article One");
        var article2 = await CreateArticleAsync(db, "Article Two");
        var trashed = await CreateArticleAsync(db, "Trashed Article");
        trashed.TrashedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CreateOfferAsync(db, "adv-1", "Acme Desks", "Standing Desk Pro");

        var ollama = new FakeOllamaService { GenerateTextResult = "OFFER_INDEX: 1\nREASON: fits." };
        var service = CreateService(db, ollama);

        var result = await service.GenerateForAllArticlesAsync();

        result.ArticlesProcessed.Should().Be(2);
        db.ArticleAffiliateSuggestions.Should().Contain(s => s.ArticleId == article1.Id);
        db.ArticleAffiliateSuggestions.Should().Contain(s => s.ArticleId == article2.Id);
        db.ArticleAffiliateSuggestions.Should().NotContain(s => s.ArticleId == trashed.Id);
    }

    [Fact]
    public async Task GenerateForUnmatchedArticlesAsync_ShouldSkipArticles_WithExistingSuggestionsOrPlacements()
    {
        await using var db = await CreateDbAsync();
        var alreadySuggested = await CreateArticleAsync(db, "Already Suggested");
        var alreadyPlaced = await CreateArticleAsync(db, "Already Placed");
        var unmatched = await CreateArticleAsync(db, "Unmatched Article");
        await CreateOfferAsync(db, "adv-1", "Acme Desks", "Standing Desk Pro");

        db.ArticleAffiliateSuggestions.Add(new ArticleAffiliateSuggestion { ArticleId = alreadySuggested.Id, AdvertiserName = "X", Status = ArticleAffiliateSuggestionStatuses.Dismissed });
        db.ArticleAffiliatePlacements.Add(new ArticleAffiliatePlacement { ArticleId = alreadyPlaced.Id, SlotToken = "{{CJ_AD_1}}", AdvertiserName = "Y" });
        await db.SaveChangesAsync();

        var ollama = new FakeOllamaService { GenerateTextResult = "OFFER_INDEX: 1\nREASON: fits." };
        var service = CreateService(db, ollama);

        var result = await service.GenerateForUnmatchedArticlesAsync();

        result.ArticlesProcessed.Should().Be(1);
        db.ArticleAffiliateSuggestions.Should().Contain(s => s.ArticleId == unmatched.Id);
        db.ArticleAffiliateSuggestions.Should().NotContain(s => s.ArticleId == alreadyPlaced.Id);
    }

    [Fact]
    public async Task ApplySuggestionAsync_ShouldCreatePlacement_InsertSlotToken_AndMarkApplied()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Applyable Article", body: "Para one.\n\nPara two.\n\nPara three.\n\nPara four.");
        var suggestion = new ArticleAffiliateSuggestion
        {
            ArticleId = article.Id,
            AdvertiserId = "adv-1",
            AdvertiserName = "Acme Desks",
            TrackingUrl = "https://example.com/track",
            Status = ArticleAffiliateSuggestionStatuses.Pending,
            Rank = 1
        };
        db.ArticleAffiliateSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeOllamaService());
        await service.ApplySuggestionAsync(suggestion.Id);

        var updated = await db.ArticleAffiliateSuggestions.SingleAsync(s => s.Id == suggestion.Id);
        updated.Status.Should().Be(ArticleAffiliateSuggestionStatuses.Applied);

        var placement = db.ArticleAffiliatePlacements.Single(p => p.ArticleId == article.Id);
        placement.AdvertiserName.Should().Be("Acme Desks");

        var refreshedArticle = await db.Articles.SingleAsync(a => a.Id == article.Id);
        refreshedArticle.BodyMarkdown.Should().Contain(placement.SlotToken);
    }

    [Fact]
    public async Task ApplySuggestionAsync_ShouldBeNoOp_WhenSuggestionAlreadyApplied()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Already Applied Article");
        var suggestion = new ArticleAffiliateSuggestion
        {
            ArticleId = article.Id,
            AdvertiserName = "Acme Desks",
            Status = ArticleAffiliateSuggestionStatuses.Applied,
            Rank = 1
        };
        db.ArticleAffiliateSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeOllamaService());
        await service.ApplySuggestionAsync(suggestion.Id);

        db.ArticleAffiliatePlacements.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAllForArticleAsync_ShouldApplyEveryPendingSuggestion_InRankOrder()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Multi Suggestion Article", body: "P1.\n\nP2.\n\nP3.\n\nP4.\n\nP5.");
        db.ArticleAffiliateSuggestions.AddRange(
            new ArticleAffiliateSuggestion { ArticleId = article.Id, AdvertiserName = "First", Status = ArticleAffiliateSuggestionStatuses.Pending, Rank = 1 },
            new ArticleAffiliateSuggestion { ArticleId = article.Id, AdvertiserName = "Second", Status = ArticleAffiliateSuggestionStatuses.Pending, Rank = 2 });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeOllamaService());
        await service.ApplyAllForArticleAsync(article.Id);

        db.ArticleAffiliatePlacements.Where(p => p.ArticleId == article.Id).Should().HaveCount(2);
        db.ArticleAffiliateSuggestions.Where(s => s.ArticleId == article.Id).Should().OnlyContain(s => s.Status == ArticleAffiliateSuggestionStatuses.Applied);
    }

    [Fact]
    public async Task DismissSuggestionAsync_ShouldMarkDismissed_WithoutCreatingPlacement()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Dismiss Article");
        var suggestion = new ArticleAffiliateSuggestion
        {
            ArticleId = article.Id,
            AdvertiserName = "Acme Desks",
            Status = ArticleAffiliateSuggestionStatuses.Pending,
            Rank = 1
        };
        db.ArticleAffiliateSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeOllamaService());
        await service.DismissSuggestionAsync(suggestion.Id);

        var updated = await db.ArticleAffiliateSuggestions.SingleAsync(s => s.Id == suggestion.Id);
        updated.Status.Should().Be(ArticleAffiliateSuggestionStatuses.Dismissed);
        db.ArticleAffiliatePlacements.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPendingSuggestionsAsync_ShouldGroupByArticle_OrderedByRank()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, "Grouped Article");
        db.ArticleAffiliateSuggestions.AddRange(
            new ArticleAffiliateSuggestion { ArticleId = article.Id, AdvertiserName = "Second", Status = ArticleAffiliateSuggestionStatuses.Pending, Rank = 2 },
            new ArticleAffiliateSuggestion { ArticleId = article.Id, AdvertiserName = "First", Status = ArticleAffiliateSuggestionStatuses.Pending, Rank = 1 });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeOllamaService());
        var groups = await service.ListPendingSuggestionsAsync();

        groups.Should().ContainSingle(g => g.ArticleId == article.Id);
        var group = groups.Single(g => g.ArticleId == article.Id);
        group.Suggestions.Select(s => s.AdvertiserName).Should().ContainInOrder("First", "Second");
    }

    private static AffiliateSuggestionService CreateService(ApplicationDbContext db, FakeOllamaService ollama) =>
        new(db, ollama, Options.Create(new ContentStudioOptions { Model = "llama3.2" }), NullLogger<AffiliateSuggestionService>.Instance);

    private static async Task<Article> CreateArticleAsync(ApplicationDbContext db, string title, string body = "Some body content.")
    {
        var article = new Article { Slug = Guid.NewGuid().ToString("N"), Title = title, BodyMarkdown = body };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article;
    }

    private static async Task<AffiliateOffer> CreateOfferAsync(ApplicationDbContext db, string advertiserId, string advertiserName, string linkName)
    {
        var offer = new AffiliateOffer
        {
            Network = "CJ",
            AdvertiserId = advertiserId,
            AdvertiserName = advertiserName,
            LinkName = linkName,
            TrackingUrl = "https://example.com/track"
        };
        db.AffiliateOffers.Add(offer);
        await db.SaveChangesAsync();
        return offer;
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeOllamaService : GwsBusinessSuite.Application.Abstractions.IOllamaService
    {
        public string GenerateTextResult { get; set; } = string.Empty;

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(GenerateTextResult);

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
