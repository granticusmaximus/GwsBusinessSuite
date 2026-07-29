using FluentAssertions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class GrowthAnalyticsServiceTests
{
    [Fact]
    public async Task RecordAsync_ShouldPersistOnlyMinimizedFirstPartyDimensions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new GrowthAnalyticsService(fixture.Db);

        await service.RecordAsync(
            new(
                "pageview",
                "visitor-1",
                "session-1",
                "/blog/privacy?secret=not-stored",
                "Privacy",
                "https://search.example/results?q=private",
                "newsletter",
                "email",
                "launch",
                0),
            "Mozilla/5.0 Mobile Edg/140.0");

        var stored = await fixture.Db.WebAnalyticsEvents.SingleAsync();
        stored.Path.Should().Be("/blog/privacy");
        stored.ReferrerHost.Should().Be("search.example");
        stored.DeviceType.Should().Be("Mobile");
        stored.BrowserFamily.Should().Be("Edge");
        stored.Should().NotBeNull();
        typeof(WebAnalyticsEvent).GetProperty("IpAddress").Should().BeNull();
        typeof(WebAnalyticsEvent).GetProperty("UserAgent").Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_ShouldIgnoreAuthenticatedAdminRoutes()
    {
        await using var fixture = await Fixture.CreateAsync();

        await new GrowthAnalyticsService(fixture.Db).RecordAsync(
            new("pageview", "visitor", "session", "/admin/growth", null, null, null, null, null, 0),
            "Mozilla/5.0");

        fixture.Db.WebAnalyticsEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAggregateAudienceAcquisitionAndEngagement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "visitor-a", "session-a", "/", now.AddMinutes(-10), source: "linkedin", campaign: "launch"),
            Event("pageview", "visitor-a", "session-a", "/about", now.AddMinutes(-9), source: "linkedin", campaign: "launch"),
            Event("engagement", "visitor-a", "session-a", "/about", now.AddMinutes(-8), engagement: 42),
            Event("pageview", "visitor-b", "session-b", "/", now.AddMinutes(-7)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db)
            .GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1));

        dashboard.Visitors.Should().Be(2);
        dashboard.PageViews.Should().Be(3);
        dashboard.Sessions.Should().Be(2);
        dashboard.BounceRate.Should().Be(50);
        dashboard.ViewsPerSession.Should().Be(1.5m);
        dashboard.AverageEngagement.Should().Be(TimeSpan.FromSeconds(42));
        dashboard.TopSources.Should().Contain(row => row.Label == "linkedin" && row.Views == 2);
        dashboard.Campaigns.Should().ContainSingle(row => row.Label == "launch" && row.Views == 2);
    }

    private static WebAnalyticsEvent Event(
        string name,
        string visitor,
        string session,
        string path,
        DateTimeOffset createdAt,
        string source = "",
        string campaign = "",
        int engagement = 0) => new()
        {
            EventName = name,
            VisitorKey = visitor,
            SessionKey = session,
            Path = path,
            Source = source,
            Campaign = campaign,
            EngagementSeconds = engagement,
            CreatedAt = createdAt,
            OccurredAtUnixSeconds = createdAt.ToUnixTimeSeconds()
        };

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db) : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; } = db;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
