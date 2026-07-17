using FluentAssertions;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Tests;

public sealed class AffiliateAnalyticsServiceTests
{
    [Fact]
    public async Task RecordClickAsync_ShouldLogClick_AndReturnTrackingUrl()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var placement = new ArticleAffiliatePlacement
        {
            ArticleId = article.Id,
            SlotToken = "{{CJ_AD_1}}",
            AdvertiserId = "adv-1",
            AdvertiserName = "Acme Tools",
            TrackingUrl = "https://example.com/track"
        };
        db.ArticleAffiliatePlacements.Add(placement);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var destination = await service.RecordClickAsync(placement.Id);

        destination.Should().Be("https://example.com/track");
        db.ArticleAffiliateClicks.Should().ContainSingle(c => c.PlacementId == placement.Id && c.AdvertiserName == "Acme Tools");
    }

    [Fact]
    public async Task RecordClickAsync_ShouldReturnNull_WhenPlacementDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var destination = await service.RecordClickAsync(Guid.NewGuid());

        destination.Should().BeNull();
        db.ArticleAffiliateClicks.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordClickAsync_ShouldTrackDurableRotationAssignment()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var now = DateTimeOffset.UtcNow;
        var rotation = new ArticleAffiliateRotation
        {
            ArticleId = article.Id,
            AdvertiserId = "adv-rotation",
            AdvertiserName = "Rotating Partner",
            TrackingUrl = "https://example.com/rotation",
            StartsAt = now,
            StartsAtUnixSeconds = now.ToUnixTimeSeconds(),
            ExpiresAt = now.AddHours(48),
            ExpiresAtUnixSeconds = now.AddHours(48).ToUnixTimeSeconds()
        };
        db.ArticleAffiliateRotations.Add(rotation);
        await db.SaveChangesAsync();

        var destination = await CreateService(db).RecordClickAsync(rotation.Id);

        destination.Should().Be("https://example.com/rotation");
        db.ArticleAffiliateClicks.Should().ContainSingle(click =>
            click.PlacementId == rotation.Id && click.AdvertiserId == "adv-rotation");
    }

    [Fact]
    public async Task RecordClickAsync_ShouldReturnNull_WhenTrackingUrlIsBlank()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var placement = new ArticleAffiliatePlacement
        {
            ArticleId = article.Id,
            SlotToken = "{{CJ_AD_1}}",
            AdvertiserId = "adv-1",
            AdvertiserName = "Acme Tools",
            TrackingUrl = string.Empty
        };
        db.ArticleAffiliatePlacements.Add(placement);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var destination = await service.RecordClickAsync(placement.Id);

        destination.Should().BeNull();
        db.ArticleAffiliateClicks.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAggregateClicksByAdvertiserAndArticle()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        // Two distinct placements (not the same one clicked twice) so this test measures
        // dashboard aggregation, independent of RecordClickAsync's own per-placement
        // click dedup (covered separately below).
        var placementA = new ArticleAffiliatePlacement
        {
            ArticleId = article.Id,
            SlotToken = "{{CJ_AD_1}}",
            AdvertiserId = "adv-1",
            AdvertiserName = "Acme Tools",
            TrackingUrl = "https://example.com/track-a"
        };
        var placementB = new ArticleAffiliatePlacement
        {
            ArticleId = article.Id,
            SlotToken = "{{CJ_AD_2}}",
            AdvertiserId = "adv-1",
            AdvertiserName = "Acme Tools",
            TrackingUrl = "https://example.com/track-b"
        };
        db.ArticleAffiliatePlacements.AddRange(placementA, placementB);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RecordClickAsync(placementA.Id);
        await service.RecordClickAsync(placementB.Id);

        var dashboard = await service.GetDashboardAsync();

        dashboard.TotalClicks.Should().Be(2);
        dashboard.ClicksByAdvertiser.Should().ContainSingle(s => s.AdvertiserId == "adv-1" && s.ClickCount == 2);
        dashboard.ClicksByArticle.Should().ContainSingle(s => s.ArticleId == article.Id && s.ClickCount == 2 && s.ArticleTitle == article.Title);
        dashboard.RecentClicks.Should().HaveCount(2);
    }

    [Fact]
    public async Task RecordClickAsync_ShouldNotLogASecondClickRow_ForTheSamePlacement_WithinTheDedupeWindow()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var placement = new ArticleAffiliatePlacement
        {
            ArticleId = article.Id,
            SlotToken = "{{CJ_AD_1}}",
            AdvertiserId = "adv-1",
            AdvertiserName = "Acme Tools",
            TrackingUrl = "https://example.com/track"
        };
        db.ArticleAffiliatePlacements.Add(placement);
        await db.SaveChangesAsync();

        // Same service/cache instance for both calls - simulates a browser back-button
        // or refresh hitting the redirect twice in quick succession.
        var service = CreateService(db);

        var firstDestination = await service.RecordClickAsync(placement.Id);
        var secondDestination = await service.RecordClickAsync(placement.Id);

        firstDestination.Should().Be("https://example.com/track");
        secondDestination.Should().Be("https://example.com/track");
        db.ArticleAffiliateClicks.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAggregateRevenueByAdvertiser_FromCommissionRecords()
    {
        await using var db = await CreateDbAsync();
        db.CjCommissionRecords.AddRange(
            new CjCommissionRecord { ExternalId = "c1", AdvertiserId = "adv-1", AdvertiserName = "Acme Tools", SaleAmount = 100m, CommissionAmount = 10m, Currency = "USD" },
            new CjCommissionRecord { ExternalId = "c2", AdvertiserId = "adv-1", AdvertiserName = "Acme Tools", SaleAmount = 50m, CommissionAmount = 5m, Currency = "USD" });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var dashboard = await service.GetDashboardAsync();

        dashboard.TotalCommissionAmount.Should().Be(15m);
        dashboard.RevenueByAdvertiser.Should().ContainSingle(s =>
            s.AdvertiserId == "adv-1" && s.TransactionCount == 2 && s.TotalSaleAmount == 150m && s.TotalCommissionAmount == 15m);
    }

    private static AffiliateAnalyticsService CreateService(ApplicationDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    private static async Task<Article> CreateArticleAsync(ApplicationDbContext db)
    {
        var article = new Article { Slug = "test-article", Title = "Test Article" };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article;
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
}
