using FluentAssertions;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Tests;

// Regression guard for a real gap: this is the executive summary shown first, aggregated
// across four other already-tested services, and it had zero test coverage of its own -
// the one place a wiring mistake (wrong field summed, wrong service queried) would go
// unnoticed despite each underlying service being individually correct.
public sealed class MissionControlServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ShouldAggregateAcrossAutomationCrmAffiliateAndDocker()
    {
        await using var db = await CreateDbAsync();

        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var automationWorkflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var workflow = await automationWorkflowService.CreateAsync("Nightly sync");
        await SeedFailedExecutionAsync(db, workflow.Id, "SKU is required.");

        var crmService = new CrmService(db, cache: new MemoryCache(new MemoryCacheOptions()));
        var contact = new Contact { FullName = "Jordan Lee", FollowUpDate = DateTimeOffset.UtcNow.AddDays(-1) };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.Deals.Add(new Deal { ContactId = contact.Id, Title = "Renewal", Stage = DealStages.Qualified, ValueUsd = 4_500m });
        db.Deals.Add(new Deal { ContactId = contact.Id, Title = "Lost one", Stage = DealStages.Lost, ValueUsd = 9_999m });
        await db.SaveChangesAsync();

        var affiliateService = new AffiliateAnalyticsService(db, new MemoryCache(new MemoryCacheOptions()));
        var article = new Article { Slug = "test-article", Title = "Test Article" };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        db.ArticleAffiliateClicks.Add(new ArticleAffiliateClick
        {
            ArticleId = article.Id, PlacementId = Guid.NewGuid(), AdvertiserId = "adv-1",
            AdvertiserName = "Acme", TrackingUrl = "https://example.com", CreatedAt = now,
            CreatedAtUnixSeconds = now.ToUnixTimeSeconds(), PassedBotFilter = true
        });
        db.CjCommissionRecords.Add(new CjCommissionRecord
        {
            ExternalId = "c1", AdvertiserId = "adv-1", AdvertiserName = "Acme", SaleAmount = 100m,
            CommissionAmount = 12.5m, Currency = "USD", CreatedAt = now, CreatedAtUnixSeconds = now.ToUnixTimeSeconds()
        });
        await db.SaveChangesAsync();

        var dockerHealthService = new DockerHealthService(db);
        db.DockerHealthAlerts.Add(new DockerHealthAlert { ContainerName = "gwssuite-web", Message = "Restarted unexpectedly.", IsRead = false });
        db.DockerHealthAlerts.Add(new DockerHealthAlert { ContainerName = "gwssuite-web", Message = "Old, already seen.", IsRead = true });
        await db.SaveChangesAsync();

        var missionControl = new MissionControlService(
            automationWorkflowService, crmService, affiliateService, dockerHealthService, TimeProvider.System);

        var snapshot = await missionControl.GetSnapshotAsync();

        snapshot.AutomationRecentFailureCount.Should().Be(1);
        snapshot.AutomationRecentFailures.Should().ContainSingle(f => f.WorkflowName == "Nightly sync");
        snapshot.CrmOpenDealCount.Should().Be(1, "the Lost deal must not count as open");
        snapshot.CrmOpenDealValueUsd.Should().Be(4_500m);
        snapshot.CrmDueFollowUpCount.Should().Be(1);
        snapshot.AffiliateTotalClicks.Should().Be(1);
        snapshot.AffiliateTotalCommissionAmount.Should().Be(12.5m);
        snapshot.DockerUnreadAlertCount.Should().Be(1, "the already-read alert must not count");
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnAllZeros_ForABrandNewInstallWithNoData()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var automationWorkflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var crmService = new CrmService(db, cache: new MemoryCache(new MemoryCacheOptions()));
        var affiliateService = new AffiliateAnalyticsService(db, new MemoryCache(new MemoryCacheOptions()));
        var dockerHealthService = new DockerHealthService(db);
        var missionControl = new MissionControlService(
            automationWorkflowService, crmService, affiliateService, dockerHealthService, TimeProvider.System);

        var snapshot = await missionControl.GetSnapshotAsync();

        snapshot.AutomationRecentFailureCount.Should().Be(0);
        snapshot.AutomationRecentFailures.Should().BeEmpty();
        snapshot.CrmOpenDealCount.Should().Be(0);
        snapshot.CrmOpenDealValueUsd.Should().Be(0m);
        snapshot.CrmDueFollowUpCount.Should().Be(0);
        snapshot.AffiliateTotalClicks.Should().Be(0);
        snapshot.AffiliateTotalCommissionAmount.Should().Be(0m);
        snapshot.DockerUnreadAlertCount.Should().Be(0);
    }

    private static async Task<Guid> SeedFailedExecutionAsync(ApplicationDbContext db, Guid workflowId, string errorMessage)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var execution = new AutomationExecution
        {
            WorkflowId = workflowId,
            Mode = AutomationExecutionModes.Manual,
            Status = AutomationExecutionStatuses.Failed,
            ErrorMessage = errorMessage,
            FinishedAt = finishedAt,
            FinishedAtUnixSeconds = finishedAt.ToUnixTimeSeconds(),
            CreatedBy = "test"
        };
        db.AutomationExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution.Id;
    }

    private sealed class FakeHttpClient : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
}
