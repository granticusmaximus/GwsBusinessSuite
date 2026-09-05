using FluentAssertions;

using GwsBusinessSuite.Application.DeveloperApi;

using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Covers the business-data reads the native Mac SentinelGPT tab grounds its answers on. These
// run against real SQLite rather than an in-memory provider on purpose: every one of them sorts
// or aggregates over a DateTimeOffset or decimal column, which is exactly where this repo has
// been bitten before - EF Core cannot translate those against SQLite, so a query written the
// obvious way throws at runtime while passing against any other provider.
public sealed class DeveloperApiSentinelReadServiceTests
{
    [Fact]
    public async Task SearchCrmAsync_MatchesContactsOnNameEmailOrCompanyAndDealsOnTitle()
    {
        await using var db = await CreateDbAsync();
        var acme = await AddContactAsync(db, "Dana Reyes", "dana@acme.io", "Acme Corp");
        await AddContactAsync(db, "Unrelated Person", "nobody@example.com", "Other Ltd");
        await AddDealAsync(db, acme.Id, "Acme platform rollout", DealStages.Qualified, 25_000m);
        await AddDealAsync(db, acme.Id, "Nothing to do with it", DealStages.Lead, 100m);
        var service = CreateService(db);

        var byName = await service.SearchCrmAsync("Dana");
        var byCompany = await service.SearchCrmAsync("Acme");

        byName.Contacts.Should().ContainSingle(contact => contact.FullName == "Dana Reyes");
        byCompany.Contacts.Should().ContainSingle();
        byCompany.Deals.Should().ContainSingle(deal => deal.Title == "Acme platform rollout");
        byCompany.Deals[0].ContactName.Should().Be("Dana Reyes", "a deal is far more useful with the person attached");
        byCompany.Deals[0].ValueUsd.Should().Be(25_000m);
    }

    [Fact]
    public async Task SearchCrmAsync_ExcludesTrashedContactsAndReturnsNothingForABlankQuery()
    {
        await using var db = await CreateDbAsync();
        var trashed = await AddContactAsync(db, "Deleted Person", "gone@acme.io", "Acme Corp");
        trashed.TrashedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var results = await service.SearchCrmAsync("Acme");
        var blank = await service.SearchCrmAsync("   ");

        results.Contacts.Should().BeEmpty("a trashed contact must not be surfaced to the assistant");
        blank.Contacts.Should().BeEmpty();
        blank.Deals.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPipelineAsync_TotalsValuePerStageAndSeparatesOpenFromWonAndLost()
    {
        await using var db = await CreateDbAsync();
        var contact = await AddContactAsync(db, "Pipeline Person", "p@example.com", "Co");
        await AddDealAsync(db, contact.Id, "A", DealStages.Lead, 1_000m);
        await AddDealAsync(db, contact.Id, "B", DealStages.Lead, 2_500m);
        await AddDealAsync(db, contact.Id, "C", DealStages.Negotiation, 10_000m);
        await AddDealAsync(db, contact.Id, "D", DealStages.Won, 40_000m);
        await AddDealAsync(db, contact.Id, "E", DealStages.Lost, 7_000m);
        var service = CreateService(db);

        var pipeline = await service.GetPipelineAsync();

        pipeline.OpenCount.Should().Be(3, "won and lost are closed, not open");
        pipeline.OpenValueUsd.Should().Be(13_500m);
        pipeline.WonValueUsd.Should().Be(40_000m);
        pipeline.LostValueUsd.Should().Be(7_000m);
        var lead = pipeline.Stages.Single(stage => stage.Stage == DealStages.Lead);
        lead.Count.Should().Be(2);
        lead.TotalValueUsd.Should().Be(3_500m);
        // Reported in the pipeline's own order, so "what's furthest along" reads correctly -
        // not alphabetically, which would put Lost and Negotiation before Won.
        pipeline.Stages.Select(stage => stage.Stage).Should()
            .Equal(DealStages.Lead, DealStages.Negotiation, DealStages.Won, DealStages.Lost);
    }

    [Fact]
    public async Task GetPipelineAsync_ReturnsZeroTotalsRatherThanFailingOnAnEmptyPipeline()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var pipeline = await service.GetPipelineAsync();

        pipeline.Stages.Should().BeEmpty();
        pipeline.OpenCount.Should().Be(0);
        pipeline.OpenValueUsd.Should().Be(0m);
    }

    [Fact]
    public async Task SearchCmsPagesAsync_ReturnsPublishStatusNewestFirstAndSkipsTrashed()
    {
        await using var db = await CreateDbAsync();
        var siteId = await AddSiteAsync(db);
        await AddCmsPageAsync(db, siteId, "Pricing", "pricing", CmsPageStatuses.Published, DateTimeOffset.UtcNow.AddDays(-10));
        await AddCmsPageAsync(db, siteId, "Pricing FAQ", "pricing-faq", CmsPageStatuses.Published, DateTimeOffset.UtcNow.AddDays(-1));
        await AddCmsPageAsync(db, siteId, "Pricing draft", "pricing-draft", CmsPageStatuses.Draft, publishedAt: null);
        var trashed = await AddCmsPageAsync(db, siteId, "Pricing old", "pricing-old", CmsPageStatuses.Published, DateTimeOffset.UtcNow);
        trashed.TrashedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var pages = await service.SearchCmsPagesAsync("pricing");

        pages.Should().HaveCount(3);
        pages[0].Slug.Should().Be("pricing-faq", "most recently published first");
        pages.Should().NotContain(page => page.Slug == "pricing-old");
        pages.Single(page => page.Slug == "pricing-draft").Status.Should().Be(CmsPageStatuses.Draft);
    }

    [Fact]
    public async Task GetSystemHealthAsync_ReportsUnreadAlertsNewestFirst()
    {
        await using var db = await CreateDbAsync();
        await AddAlertAsync(db, "web", "Exited with code 137", isRead: false, DateTimeOffset.UtcNow.AddHours(-5));
        await AddAlertAsync(db, "db", "Restarting repeatedly", isRead: false, DateTimeOffset.UtcNow);
        await AddAlertAsync(db, "cache", "Old news", isRead: true, DateTimeOffset.UtcNow.AddDays(-1));
        var service = CreateService(db);

        var health = await service.GetSystemHealthAsync();

        health.UnreadAlertCount.Should().Be(2, "an acknowledged alert is not a current problem");
        health.RecentAlerts.Should().HaveCount(2);
        health.RecentAlerts[0].ContainerName.Should().Be("db");
        health.RecentAlerts.Should().NotContain(alert => alert.ContainerName == "cache");
    }

    [Fact]
    public async Task GetSystemHealthAsync_ReportsAHealthySystemAsNoAlertsRatherThanAnError()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var health = await service.GetSystemHealthAsync();

        health.UnreadAlertCount.Should().Be(0);
        health.RecentAlerts.Should().BeEmpty();
    }

    // Wiki search and per-page ACLs belong to the two wiki reads only; none of the CRM/CMS/health
    // methods under test touch either collaborator. Passed as null rather than stubbed because
    // ISentinelWorkspaceService and ISentinelAccessService are broad interfaces (10+ and 15+
    // members) whose stubs would be pure noise here - and because null fails loudly the moment
    // one of these methods starts depending on them, which a silently-empty stub would hide.
    private static IDeveloperApiSentinelReadService CreateService(ApplicationDbContext db) =>
        new DeveloperApiSentinelReadService(db, null!, null!);

    private static async Task<Contact> AddContactAsync(
        ApplicationDbContext db, string name, string email, string company)
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            FullName = name,
            Email = email,
            Company = company,
            Status = ContactStatuses.Lead,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tester"
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return contact;
    }

    private static async Task<Deal> AddDealAsync(
        ApplicationDbContext db, Guid contactId, string title, string stage, decimal value)
    {
        var deal = new Deal
        {
            Id = Guid.NewGuid(),
            ContactId = contactId,
            Title = title,
            Stage = stage,
            ValueUsd = value,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CreatedBy = "tester"
        };
        db.Deals.Add(deal);
        await db.SaveChangesAsync();
        return deal;
    }

    private static async Task<Guid> AddSiteAsync(ApplicationDbContext db)
    {
        var site = new CmsSite
        {
            Id = Guid.NewGuid(),
            Name = "Test site",
            Slug = "test-site",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tester"
        };
        db.CmsSites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    private static async Task<CmsPage> AddCmsPageAsync(
        ApplicationDbContext db, Guid siteId, string title, string slug, string status, DateTimeOffset? publishedAt)
    {
        var page = new CmsPage
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            Title = title,
            Slug = slug,
            Status = status,
            PublishedAt = publishedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tester"
        };
        db.CmsPages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private static async Task AddAlertAsync(
        ApplicationDbContext db, string container, string message, bool isRead, DateTimeOffset createdAt)
    {
        db.DockerHealthAlerts.Add(new DockerHealthAlert
        {
            Id = Guid.NewGuid(),
            ContainerName = container,
            Severity = DockerHealthAlertSeverity.Error,
            Message = message,
            IsRead = isRead,
            CreatedAt = createdAt,
            CreatedBy = "monitor"
        });
        await db.SaveChangesAsync();
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
