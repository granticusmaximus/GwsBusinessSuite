using FluentAssertions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class SanityPublisherWorkspaceServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ShouldSplitQueueFromRecentlyPublishedDrafts()
    {
        await using var db = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;

        var readyDraft = new SeoArticleDraft
        {
            Topic = "Ready Draft",
            TargetAudience = "Developers",
            Status = SeoArticleDraftStatuses.Approved,
            Title = "Ready Draft",
            Slug = "ready-draft",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-1),
            ApprovedAt = now.AddDays(-1),
            CreatedBy = "tests"
        };

        var needsBackupDraft = new SeoArticleDraft
        {
            Topic = "Needs Backup",
            TargetAudience = "Developers",
            Status = SeoArticleDraftStatuses.Approved,
            Title = "Needs Backup",
            Slug = "needs-backup",
            CreatedAt = now.AddDays(-5),
            UpdatedAt = now.AddHours(-2),
            ApprovedAt = now.AddDays(-4),
            CreatedBy = "tests"
        };

        var publishedDraft = new SeoArticleDraft
        {
            Topic = "Published",
            TargetAudience = "Developers",
            Status = SeoArticleDraftStatuses.Approved,
            Title = "Published",
            Slug = "published",
            CreatedAt = now.AddDays(-7),
            UpdatedAt = now.AddDays(-3),
            ApprovedAt = now.AddDays(-6),
            CreatedBy = "tests"
        };

        var blockedDraft = new SeoArticleDraft
        {
            Topic = "Blocked",
            TargetAudience = "Developers",
            Status = SeoArticleDraftStatuses.PendingReview,
            Title = "Blocked",
            Slug = "blocked",
            CreatedAt = now.AddDays(-1),
            CreatedBy = "tests"
        };

        db.SeoArticleDrafts.AddRange(readyDraft, needsBackupDraft, publishedDraft, blockedDraft);
        await db.SaveChangesAsync();

        db.SeoArticleAffiliatePlacements.Add(new SeoArticleAffiliatePlacement
        {
            SeoArticleDraftId = readyDraft.Id,
            SlotToken = "{{CJ_AD_SLOT_1}}",
            AdvertiserId = "123",
            AdvertiserName = "Partner",
            Category = "Dev Tools",
            TrackingUrl = "https://example.com",
            CreatedAt = now,
            CreatedBy = "tests"
        });

        db.SeoArticleWorkflowEvents.AddRange(
            new SeoArticleWorkflowEvent
            {
                SeoArticleDraftId = needsBackupDraft.Id,
                EventType = SeoArticleWorkflowEventTypes.BackedUpToSanity,
                Notes = "First publish",
                CreatedAt = now.AddDays(-2),
                CreatedBy = "tests"
            },
            new SeoArticleWorkflowEvent
            {
                SeoArticleDraftId = publishedDraft.Id,
                EventType = SeoArticleWorkflowEventTypes.PublishedToSanity,
                Notes = "Current publish",
                CreatedAt = now.AddDays(-1),
                CreatedBy = "tests"
            });

        await db.SaveChangesAsync();

        var service = new SanityPublisherWorkspaceService(
            db,
            Options.Create(new SanityOptions
            {
                ProjectId = "demo-project",
                Dataset = "production",
                Token = "secret",
                DocumentType = "seoArticle",
                DocumentIdPrefix = "gws-seo-"
            }));

        var snapshot = await service.GetSnapshotAsync();

        snapshot.Configuration.IsReady.Should().BeTrue();
        snapshot.PublicationQueue.Should().HaveCount(2);
        snapshot.PublicationQueue.Should().ContainSingle(item => item.Title == "Ready Draft" && item.PublishState == "Ready");
        snapshot.PublicationQueue.Should().ContainSingle(item => item.Title == "Needs Backup" && item.PublishState == "Needs Backup");
        snapshot.RecentlyPublished.Should().HaveCount(2);
        snapshot.RecentlyPublished.Should().ContainSingle(item => item.Title == "Published" && item.PublishState == "Backed Up");
        snapshot.PublicationQueue.Should().ContainSingle(item => item.Title == "Ready Draft" && item.AffiliatePlacementCount == 1);
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
