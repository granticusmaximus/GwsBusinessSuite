using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GwsBusinessSuite.Tests;

public sealed class PerformanceInfrastructureTests
{
    [Fact]
    public async Task NewsTimestampMigration_ShouldBackfillExistingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20260716235006_AddMediaAssetThumbnail");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "NewsItems"
                ("Id", "TopicId", "Title", "Url", "Source", "PublishedAt", "Description",
                 "OllamaSummary", "ImageUrl", "FetchedAt", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy")
            VALUES
                ({{id}}, NULL, 'Existing story', 'https://example.com/existing', 'Example',
                 '2026-07-17 18:00:00+00:00', '', '', NULL,
                 '2026-07-17 19:00:00+00:00', '2026-07-17 19:00:00+00:00', '', NULL, NULL)
            """);

        await migrator.MigrateAsync();

        var item = await db.NewsItems.AsNoTracking().SingleAsync(row => row.Id == id);
        item.FetchedAtUnixSeconds.Should().Be(new DateTimeOffset(2026, 7, 17, 19, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        item.PublishedAtUnixSeconds.Should().Be(new DateTimeOffset(2026, 7, 17, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task PublicContentInterceptor_ShouldInvalidateOnlyForPublicContentWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var invalidator = new CountingInvalidator();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new PublicContentCacheInvalidationInterceptor(invalidator))
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Contacts.Add(new Contact { FullName = "Private CRM record" });
        await db.SaveChangesAsync();
        invalidator.Count.Should().Be(0);

        db.Articles.Add(new Article
        {
            Title = "Public article",
            Slug = "public-article",
            Status = ArticleStatuses.Draft
        });
        await db.SaveChangesAsync();
        invalidator.Count.Should().Be(1);
    }

    [Fact]
    public void PerformanceTelemetry_ShouldAggregateRequestLatencyByRoute()
    {
        var telemetry = new PerformanceTelemetry();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/probe";
        context.Response.StatusCode = StatusCodes.Status200OK;

        telemetry.Record(context, TimeSpan.FromMilliseconds(20));
        telemetry.Record(context, TimeSpan.FromMilliseconds(40));

        var metric = telemetry.Snapshot().Should().ContainSingle().Subject;
        metric.Route.Should().Be("GET <static-or-unmatched>");
        metric.RequestCount.Should().Be(2);
        metric.AverageMilliseconds.Should().BeApproximately(30, 0.01);
        metric.MaximumMilliseconds.Should().BeApproximately(40, 0.01);
    }

    private sealed class CountingInvalidator : IPublicContentCacheInvalidator
    {
        public int Count { get; private set; }

        public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.CompletedTask;
        }
    }
}
