using FluentAssertions;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

        var service = new AffiliateAnalyticsService(db);
        var destination = await service.RecordClickAsync(placement.Id);

        destination.Should().Be("https://example.com/track");
        db.ArticleAffiliateClicks.Should().ContainSingle(c => c.PlacementId == placement.Id && c.AdvertiserName == "Acme Tools");
    }

    [Fact]
    public async Task RecordClickAsync_ShouldReturnNull_WhenPlacementDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = new AffiliateAnalyticsService(db);

        var destination = await service.RecordClickAsync(Guid.NewGuid());

        destination.Should().BeNull();
        db.ArticleAffiliateClicks.Should().BeEmpty();
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

        var service = new AffiliateAnalyticsService(db);
        var destination = await service.RecordClickAsync(placement.Id);

        destination.Should().BeNull();
        db.ArticleAffiliateClicks.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAggregateClicksByAdvertiserAndArticle()
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

        var service = new AffiliateAnalyticsService(db);
        await service.RecordClickAsync(placement.Id);
        await service.RecordClickAsync(placement.Id);

        var dashboard = await service.GetDashboardAsync();

        dashboard.TotalClicks.Should().Be(2);
        dashboard.ClicksByAdvertiser.Should().ContainSingle(s => s.AdvertiserId == "adv-1" && s.ClickCount == 2);
        dashboard.ClicksByArticle.Should().ContainSingle(s => s.ArticleId == article.Id && s.ClickCount == 2 && s.ArticleTitle == article.Title);
        dashboard.RecentClicks.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAggregateRevenueByAdvertiser_FromCommissionRecords()
    {
        await using var db = await CreateDbAsync();
        db.CjCommissionRecords.AddRange(
            new CjCommissionRecord { ExternalId = "c1", AdvertiserId = "adv-1", AdvertiserName = "Acme Tools", SaleAmount = 100m, CommissionAmount = 10m, Currency = "USD" },
            new CjCommissionRecord { ExternalId = "c2", AdvertiserId = "adv-1", AdvertiserName = "Acme Tools", SaleAmount = 50m, CommissionAmount = 5m, Currency = "USD" });
        await db.SaveChangesAsync();

        var service = new AffiliateAnalyticsService(db);
        var dashboard = await service.GetDashboardAsync();

        dashboard.TotalCommissionAmount.Should().Be(15m);
        dashboard.RevenueByAdvertiser.Should().ContainSingle(s =>
            s.AdvertiserId == "adv-1" && s.TransactionCount == 2 && s.TotalSaleAmount == 150m && s.TotalCommissionAmount == 15m);
    }

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
