using FluentAssertions;
using GwsBusinessSuite.Application.AffiliateRotations;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class AffiliateRotationServiceTests
{
    [Fact]
    public async Task RefreshAsync_ShouldKeepAssignmentFor48Hours_ThenChooseAnotherAdvertiser()
    {
        await using var db = await CreateDbAsync();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));
        var article = new Article
        {
            Slug = "active-post",
            Title = "Active Post",
            Status = ArticleStatuses.Published,
            PublishedAt = clock.GetUtcNow().AddDays(-1)
        };
        db.Articles.Add(article);
        AddAdvertiser(db, "adv-a", "Acme", "Acme Deal", "https://example.com/acme");
        AddAdvertiser(db, "adv-b", "Beta", "Beta Deal", "https://example.com/beta");
        await db.SaveChangesAsync();

        var service = CreateService(db, clock);

        var first = await service.RefreshAsync();
        var firstRotation = await db.ArticleAffiliateRotations.AsNoTracking().SingleAsync();
        var repeated = await service.RefreshAsync();

        first.AssignmentsCreated.Should().Be(1);
        repeated.AssignmentsCreated.Should().Be(0);
        repeated.AssignmentsPreserved.Should().Be(1);
        firstRotation.ExpiresAt.Should().Be(firstRotation.StartsAt.AddHours(48));

        clock.Advance(TimeSpan.FromHours(49));
        var afterWindow = await service.RefreshAsync();
        var rotations = await db.ArticleAffiliateRotations.AsNoTracking()
            .OrderBy(rotation => rotation.StartsAtUnixSeconds)
            .ToListAsync();

        afterWindow.AssignmentsCreated.Should().Be(1);
        rotations.Should().HaveCount(2);
        rotations[1].AdvertiserId.Should().NotBe(rotations[0].AdvertiserId);
    }

    [Fact]
    public async Task RefreshAsync_ShouldOnlyUseLinksFromConnectedAdvertisers()
    {
        await using var db = await CreateDbAsync();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));
        db.Articles.Add(new Article
        {
            Slug = "active-post",
            Title = "Active Post",
            Status = ArticleStatuses.Published,
            PublishedAt = clock.GetUtcNow().AddDays(-1)
        });
        AddAdvertiser(db, "joined", "Joined", "Joined Deal", "https://example.com/joined", "joined");
        AddAdvertiser(db, "pending", "Pending", "Pending Deal", "https://example.com/pending", "pending");
        await db.SaveChangesAsync();

        var result = await CreateService(db, clock).RefreshAsync();
        var assignment = await db.ArticleAffiliateRotations.AsNoTracking().SingleAsync();

        result.EligibleOfferCount.Should().Be(1);
        assignment.AdvertiserId.Should().Be("joined");
    }

    [Fact]
    public async Task RefreshAsync_ShouldAssignEveryCurrentlyPublishedPost_AndSkipInactivePosts()
    {
        await using var db = await CreateDbAsync();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));
        db.Articles.AddRange(
            NewArticle("published-one", ArticleStatuses.Published, clock.GetUtcNow().AddDays(-2)),
            NewArticle("published-two", ArticleStatuses.Published, clock.GetUtcNow().AddMinutes(-1)),
            NewArticle("scheduled", ArticleStatuses.Published, clock.GetUtcNow().AddDays(1)),
            NewArticle("draft", ArticleStatuses.Draft, null),
            new Article
            {
                Slug = "trashed",
                Title = "Trashed",
                Status = ArticleStatuses.Published,
                PublishedAt = clock.GetUtcNow().AddDays(-1),
                TrashedAt = clock.GetUtcNow()
            });
        AddAdvertiser(db, "joined", "Joined", "Joined Deal", "https://example.com/joined");
        await db.SaveChangesAsync();

        var result = await CreateService(db, clock).RefreshAsync();
        var assignedSlugs = await db.ArticleAffiliateRotations.AsNoTracking()
            .Join(db.Articles, rotation => rotation.ArticleId, article => article.Id, (_, article) => article.Slug)
            .OrderBy(slug => slug)
            .ToListAsync();

        result.ActiveArticleCount.Should().Be(2);
        result.AssignmentsCreated.Should().Be(2);
        assignedSlugs.Should().Equal("published-one", "published-two");
    }

    [Fact]
    public async Task GetActivePlacementAsync_ShouldReturnDurableClickTrackedMarkup()
    {
        await using var db = await CreateDbAsync();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));
        var article = new Article
        {
            Slug = "active-post",
            Title = "Active Post",
            Status = ArticleStatuses.Published,
            PublishedAt = clock.GetUtcNow().AddDays(-1)
        };
        db.Articles.Add(article);
        AddAdvertiser(db, "joined", "Joined", "Joined Deal", "https://example.com/joined");
        await db.SaveChangesAsync();

        var placement = await CreateService(db, clock).GetActivePlacementAsync(article.Id);
        var rotation = await db.ArticleAffiliateRotations.AsNoTracking().SingleAsync();

        placement.Should().NotBeNull();
        placement!.Value.PlacementId.Should().Be(rotation.Id);
        placement.Value.TrackingUrl.Should().Be("https://example.com/joined");
    }

    [Fact]
    public async Task RefreshAsync_ShouldNotCreateAssignments_WhenRotationIsPaused()
    {
        await using var db = await CreateDbAsync();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));
        db.CjConnectorSettings.Add(new CjConnectorSettings
        {
            Id = CjConnectorSettings.WellKnownId,
            AutomaticArticleRotationEnabled = false
        });
        db.Articles.Add(NewArticle("published", ArticleStatuses.Published, clock.GetUtcNow().AddDays(-1)));
        AddAdvertiser(db, "joined", "Joined", "Joined Deal", "https://example.com/joined");
        await db.SaveChangesAsync();

        var result = await CreateService(db, clock).RefreshAsync();

        result.IsEnabled.Should().BeFalse();
        result.AssignmentsCreated.Should().Be(0);
        db.ArticleAffiliateRotations.Should().BeEmpty();
    }

    private static AffiliateRotationService CreateService(ApplicationDbContext db, TimeProvider clock) =>
        new(db, clock, NullLogger<AffiliateRotationService>.Instance);

    private static Article NewArticle(string slug, string status, DateTimeOffset? publishedAt) => new()
    {
        Slug = slug,
        Title = slug,
        Status = status,
        PublishedAt = publishedAt
    };

    private static void AddAdvertiser(
        ApplicationDbContext db,
        string advertiserId,
        string advertiserName,
        string linkName,
        string trackingUrl,
        string relationshipStatus = "joined")
    {
        db.AffiliateOffers.AddRange(
            new AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = advertiserId,
                AdvertiserName = advertiserName,
                LinkName = advertiserId,
                RelationshipStatus = relationshipStatus,
                CreatedBy = "test"
            },
            new AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = advertiserId,
                AdvertiserName = advertiserName,
                LinkName = linkName,
                TrackingUrl = trackingUrl,
                CreatedBy = "test"
            });
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

    private sealed class TestTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset currentUtcNow = initialUtcNow;
        public override DateTimeOffset GetUtcNow() => currentUtcNow;
        public void Advance(TimeSpan duration) => currentUtcNow = currentUtcNow.Add(duration);
    }
}
