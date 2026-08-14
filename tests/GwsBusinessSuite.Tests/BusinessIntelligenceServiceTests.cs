using FluentAssertions;
using GwsBusinessSuite.Application.BusinessIntelligence;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class BusinessIntelligenceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreviewAsync_ShouldAggregateDealCountByStageWithinRange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Buyer" };
        fixture.Db.Contacts.Add(contact);
        fixture.Db.Deals.AddRange(
            Deal(contact.Id, "Current lead", DealStages.Lead, 100, Now.AddDays(-2)),
            Deal(contact.Id, "Current lead 2", DealStages.Lead, 250, Now.AddDays(-4)),
            Deal(contact.Id, "Current win", DealStages.Won, 500, Now.AddDays(-6)),
            Deal(contact.Id, "Old", DealStages.Lost, 900, Now.AddDays(-40)));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.PreviewAsync(new BiWidgetEditor
        {
            QueryShape = BiQueryShapes.Deals,
            Metric = BiMetrics.Count,
            Dimension = BiDimensions.Stage,
            Visualization = BiVisualizations.Bar,
            RangeDays = 30
        });

        result.Total.Should().Be(3);
        result.Points.Should().ContainEquivalentOf(new BiDataPoint(DealStages.Lead, 2));
        result.Points.Should().ContainEquivalentOf(new BiDataPoint(DealStages.Won, 1));
        result.Points.Should().NotContain(point => point.Label == DealStages.Lost);
    }

    [Fact]
    public async Task PreviewAsync_ShouldAggregateDealValueByMonth()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Buyer" };
        fixture.Db.Contacts.Add(contact);
        fixture.Db.Deals.AddRange(
            Deal(contact.Id, "One", DealStages.Lead, 125, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            Deal(contact.Id, "Two", DealStages.Won, 375, new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.PreviewAsync(Editor(BiQueryShapes.Deals, BiMetrics.PipelineValue, BiDimensions.Month));

        result.ValueFormat.Should().Be("Currency");
        result.Points.Should().ContainSingle().Which.Should().Be(new BiDataPoint("Aug 2026", 500));
    }

    [Fact]
    public async Task PreviewAsync_ShouldAggregatePublishedArticleTrafficAndDistinctVisitors()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Articles.AddRange(
            new Article { Title = "Live", Slug = "live", Status = ArticleStatuses.Published },
            new Article { Title = "Draft", Slug = "draft", Status = ArticleStatuses.Draft });
        fixture.Db.WebAnalyticsEvents.AddRange(
            PageView("/blog/live", "v1", Now.AddDays(-1)),
            PageView("/blog/live", "v1", Now.AddDays(-1)),
            PageView("/blog/live", "v2", Now.AddDays(-1)),
            PageView("/blog/draft", "v3", Now.AddDays(-1)));
        await fixture.Db.SaveChangesAsync();

        var views = await fixture.Service.PreviewAsync(Editor(
            BiQueryShapes.ArticlePerformance, BiMetrics.PageViews, BiDimensions.Article));
        var visitors = await fixture.Service.PreviewAsync(Editor(
            BiQueryShapes.ArticlePerformance, BiMetrics.Visitors, BiDimensions.Article));

        views.Points.Should().ContainSingle().Which.Should().Be(new BiDataPoint("Live", 3));
        visitors.Points.Should().ContainSingle().Which.Should().Be(new BiDataPoint("Live", 2));
    }

    [Fact]
    public async Task PreviewAsync_ShouldAggregateAffiliateCommissionByAdvertiser()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.CjCommissionRecords.AddRange(
            Commission("1", "Acme", 100, 12, Now.AddDays(-1)),
            Commission("2", "Acme", 50, 8, Now.AddDays(-2)),
            Commission("3", "Other", 30, 3, Now.AddDays(-3)));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.PreviewAsync(Editor(
            BiQueryShapes.AffiliateRevenue, BiMetrics.Commission, BiDimensions.Advertiser));

        result.Total.Should().Be(23);
        result.Points[0].Should().Be(new BiDataPoint("Acme", 20));
        result.Points[1].Should().Be(new BiDataPoint("Other", 3));
    }

    [Fact]
    public async Task SaveWidgetAsync_ShouldCreateAndUpdateOnlyTheOwnersWidget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var editor = Editor(BiQueryShapes.Deals, BiMetrics.Count, BiDimensions.Stage);
        editor.Title = "Pipeline";

        var id = await fixture.Service.SaveWidgetAsync(" Alice ", editor);
        editor.Id = id;
        editor.Title = "Updated pipeline";
        await fixture.Service.SaveWidgetAsync("ALICE", editor);

        var dashboard = await fixture.Service.GetDashboardAsync("alice");
        dashboard.Should().ContainSingle().Which.Title.Should().Be("Updated pipeline");
        (await fixture.Service.GetDashboardAsync("bob")).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteWidgetAsync_ShouldFailClosedForAnotherOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var editor = Editor(BiQueryShapes.Deals, BiMetrics.Count, BiDimensions.Stage);
        editor.Title = "Private chart";
        var id = await fixture.Service.SaveWidgetAsync("alice", editor);

        var act = () => fixture.Service.DeleteWidgetAsync("bob", id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
        (await fixture.Service.GetDashboardAsync("alice")).Should().ContainSingle();
    }

    [Fact]
    public async Task PreviewAsync_ShouldRejectMetricOutsideTheSelectedSafeShape()
    {
        await using var fixture = await Fixture.CreateAsync();
        var editor = Editor(BiQueryShapes.Deals, BiMetrics.Commission, BiDimensions.Stage);

        var act = () => fixture.Service.PreviewAsync(editor);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*metric is not available*");
    }

    private static BiWidgetEditor Editor(string shape, string metric, string dimension) => new()
    {
        Title = "Test chart",
        QueryShape = shape,
        Metric = metric,
        Dimension = dimension,
        Visualization = BiVisualizations.Bar,
        RangeDays = 30
    };

    private static Deal Deal(Guid contactId, string title, string stage, decimal value, DateTimeOffset createdAt) => new()
    {
        ContactId = contactId,
        Title = title,
        Stage = stage,
        ValueUsd = value,
        CreatedAt = createdAt
    };

    private static WebAnalyticsEvent PageView(string path, string visitor, DateTimeOffset occurredAt) => new()
    {
        EventName = WebAnalyticsEventNames.PageView,
        VisitorKey = visitor,
        SessionKey = Guid.NewGuid().ToString("N"),
        Path = path,
        OccurredAtUnixSeconds = occurredAt.ToUnixTimeSeconds(),
        CreatedAt = occurredAt
    };

    private static CjCommissionRecord Commission(
        string id, string advertiser, decimal sales, decimal commission, DateTimeOffset createdAt) => new()
    {
        ExternalId = id,
        AdvertiserName = advertiser,
        SaleAmount = sales,
        CommissionAmount = commission,
        CreatedAtUnixSeconds = createdAt.ToUnixTimeSeconds(),
        CreatedAt = createdAt
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new BusinessIntelligenceService(db, new FixedTimeProvider(Now));
        }

        public ApplicationDbContext Db { get; }
        public BusinessIntelligenceService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
