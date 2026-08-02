using FluentAssertions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Net;

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
    public async Task RecordAsync_ShouldPersistOnlyCoarseResolvedGeography()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new StubGeoLocationResolver(
            new("us", "United States", "ga", "Georgia"));
        var service = new GrowthAnalyticsService(fixture.Db, resolver);
        var address = IPAddress.Parse("8.8.8.8");

        await service.RecordAsync(
            new("pageview", "visitor", "session", "/", "Home", null, null, null, null, 0),
            "Mozilla/5.0",
            address);

        var stored = await fixture.Db.WebAnalyticsEvents.SingleAsync();
        resolver.LastAddress.Should().Be(address);
        stored.CountryCode.Should().Be("US");
        stored.CountryName.Should().Be("United States");
        stored.RegionCode.Should().Be("GA");
        stored.RegionName.Should().Be("Georgia");
        typeof(WebAnalyticsEvent).GetProperty("IpAddress").Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_ShouldNotResolveGeographyForEngagementEvents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new StubGeoLocationResolver(
            new("US", "United States", "GA", "Georgia"));
        var service = new GrowthAnalyticsService(fixture.Db, resolver);

        await service.RecordAsync(
            new("engagement", "visitor", "session", "/", "Home", null, null, null, null, 12),
            "Mozilla/5.0",
            IPAddress.Parse("8.8.8.8"));

        resolver.ResolveCallCount.Should().Be(0);
        var stored = await fixture.Db.WebAnalyticsEvents.SingleAsync();
        stored.CountryCode.Should().BeEmpty();
        stored.RegionCode.Should().BeEmpty();
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

    [Fact]
    public async Task GetDashboardAsync_ShouldCompareHeadlineMetricsWithPreviousEqualPeriod()
    {
        await using var fixture = await Fixture.CreateAsync();
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7);
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "previous", "previous-session", "/", from.AddDays(-1)),
            Event("engagement", "previous", "previous-session", "/", from.AddDays(-1).AddMinutes(1), engagement: 30),
            Event("pageview", "current-a", "current-a-session", "/", from.AddDays(1)),
            Event("pageview", "current-a", "current-a-session", "/pricing", from.AddDays(1).AddMinutes(1)),
            Event("engagement", "current-a", "current-a-session", "/pricing", from.AddDays(1).AddMinutes(2), engagement: 60),
            Event("pageview", "current-b", "current-b-session", "/", from.AddDays(2)),
            Event("engagement", "current-b", "current-b-session", "/", from.AddDays(2).AddMinutes(1), engagement: 120));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db).GetDashboardAsync(from, to);

        dashboard.VisitorsComparison.Should().Be(new AnalyticsPeriodComparison(1, 100));
        dashboard.PageViewsComparison.Should().Be(new AnalyticsPeriodComparison(1, 200));
        dashboard.BounceRateComparison.Should().Be(new AnalyticsPeriodComparison(100, -50));
        dashboard.AverageEngagementComparison.Should().Be(new AnalyticsPeriodComparison(30, 200));
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldDescribeNewMetricsWithoutInfiniteChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7);
        fixture.Db.WebAnalyticsEvents.Add(
            Event("pageview", "current", "current-session", "/", from.AddDays(1)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db).GetDashboardAsync(from, to);

        dashboard.VisitorsComparison.Should().Be(new AnalyticsPeriodComparison(0, null));
        dashboard.PageViewsComparison.Should().Be(new AnalyticsPeriodComparison(0, null));
        dashboard.BounceRateComparison.Should().Be(new AnalyticsPeriodComparison(0, null));
        dashboard.AverageEngagementComparison.Should().Be(new AnalyticsPeriodComparison(0, 0));
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAllowExportToRaiseBreakdownLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.Db.WebAnalyticsEvents.AddRange(Enumerable.Range(1, 15).Select(index =>
            Event("pageview", $"visitor-{index}", $"session-{index}", $"/page-{index:D2}", now.AddMinutes(-index))));
        await fixture.Db.SaveChangesAsync();

        var service = new GrowthAnalyticsService(fixture.Db);
        var dashboard = await service.GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1));
        var exportDashboard = await service.GetDashboardAsync(
            now.AddDays(-1),
            now.AddMinutes(1),
            breakdownLimit: 1000);

        dashboard.TopPages.Should().HaveCount(12);
        exportDashboard.TopPages.Should().HaveCount(15);
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldAggregateCountryAndRegionWithoutRawAddresses()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "visitor-a", "session-a", "/", now.AddMinutes(-3),
                countryCode: "US", countryName: "United States", regionCode: "GA", regionName: "Georgia"),
            Event("pageview", "visitor-b", "session-b", "/", now.AddMinutes(-2),
                countryCode: "US", countryName: "United States", regionCode: "GA", regionName: "Georgia"),
            Event("pageview", "visitor-c", "session-c", "/", now.AddMinutes(-1),
                countryCode: "CA", countryName: "Canada", regionCode: "ON", regionName: "Ontario"));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(
                fixture.Db,
                new StubGeoLocationResolver(null, isConfigured: true))
            .GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1));

        dashboard.GeoLocationConfigured.Should().BeTrue();
        dashboard.Countries.Should().Contain(row =>
            row.Label == "United States" && row.Views == 2 && row.Share == 66.7m);
        dashboard.Countries.Should().Contain(row =>
            row.Label == "Canada" && row.Views == 1 && row.Share == 33.3m);
        dashboard.Regions.Should().Contain(row =>
            row.Label == "Georgia, US" && row.Views == 2);
        dashboard.Regions.Should().Contain(row =>
            row.Label == "Ontario, CA" && row.Views == 1);
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldReportNewReturningVisitorsAndRetentionCohorts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var from = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7).AddMinutes(1);
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "returning", "old-session", "/", from.AddDays(-3)),
            Event("pageview", "returning", "return-session", "/", from.AddHours(1)),
            Event("pageview", "new-a", "new-a-first", "/", from.AddHours(2)),
            Event("pageview", "new-b", "new-b-first", "/", from.AddHours(3)),
            Event("pageview", "new-a", "new-a-return", "/blog", from.AddDays(2).AddHours(1)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db).GetDashboardAsync(from, to);

        dashboard.Visitors.Should().Be(3);
        dashboard.NewVisitors.Should().Be(2);
        dashboard.ReturningVisitors.Should().Be(1);
        dashboard.ReturningVisitorRate.Should().BeApproximately(33.3m, 0.01m);
        dashboard.RetentionPeriodLabel.Should().Be("Day");
        var cohort = dashboard.RetentionCohorts.Should().ContainSingle().Which;
        cohort.CohortStart.Should().Be(new DateOnly(2026, 7, 1));
        cohort.Visitors.Should().Be(2);
        cohort.Periods.Should().HaveCount(7);
        cohort.Periods[0].Should().BeEquivalentTo(new { Visitors = (int?)2, Rate = (decimal?)100m });
        cohort.Periods[2].Should().BeEquivalentTo(new { Visitors = (int?)1, Rate = (decimal?)50m });
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldReportEventGoalsByConvertingSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.Db.AnalyticsGoals.Add(new AnalyticsGoal
        {
            Name = "Newsletter signup",
            MatchType = AnalyticsGoalMatchTypes.Event,
            MatchValue = "newsletter_signup"
        });
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "visitor-a", "session-a", "/", now.AddMinutes(-10), source: "linkedin"),
            Event("newsletter_signup", "visitor-a", "session-a", "/", now.AddMinutes(-9), source: "linkedin"),
            Event("newsletter_signup", "visitor-a", "session-a", "/", now.AddMinutes(-8), source: "linkedin"),
            Event("pageview", "visitor-b", "session-b", "/", now.AddMinutes(-7), source: "email"));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db)
            .GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1));

        dashboard.TotalConversions.Should().Be(2);
        dashboard.OverallConversionRate.Should().Be(50);
        dashboard.Goals.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Name = "Newsletter signup",
            Conversions = 2,
            ConvertingVisitors = 1,
            ConversionRate = 50m,
            TopSource = "linkedin"
        });
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldMatchExactAndWildcardPageGoals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.Db.AnalyticsGoals.AddRange(
            new AnalyticsGoal
            {
                Name = "Lead complete",
                MatchType = AnalyticsGoalMatchTypes.PagePath,
                MatchValue = "/thank-you"
            },
            new AnalyticsGoal
            {
                Name = "Checkout complete",
                MatchType = AnalyticsGoalMatchTypes.PagePath,
                MatchValue = "/checkout/success/*"
            });
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "visitor-a", "session-a", "/thank-you", now.AddMinutes(-6)),
            Event("pageview", "visitor-b", "session-b", "/thank-you/again", now.AddMinutes(-5)),
            Event("pageview", "visitor-c", "session-c", "/checkout/success/order-12", now.AddMinutes(-4)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db)
            .GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1));

        dashboard.Goals.Single(goal => goal.Name == "Lead complete").Conversions.Should().Be(1);
        dashboard.Goals.Single(goal => goal.Name == "Checkout complete").Conversions.Should().Be(1);
        dashboard.OverallConversionRate.Should().BeApproximately(66.7m, 0.01m);
    }

    [Fact]
    public async Task GoalManagement_ShouldNormalizeRejectDuplicatesAndRetainAnalyticsHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new GrowthAnalyticsService(fixture.Db);
        fixture.Db.WebAnalyticsEvents.Add(
            Event("pageview", "visitor", "session", "/thank-you", DateTimeOffset.UtcNow));
        await fixture.Db.SaveChangesAsync();

        await service.SaveGoalAsync(new(null, "Lead", AnalyticsGoalMatchTypes.PagePath, "thank-you?source=form", true));
        var stored = await fixture.Db.AnalyticsGoals.SingleAsync();
        stored.MatchValue.Should().Be("/thank-you");

        var duplicate = () => service.SaveGoalAsync(
            new(null, "Lead duplicate", AnalyticsGoalMatchTypes.PagePath, "/thank-you", true));
        await duplicate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already tracks*");

        await service.DeleteGoalAsync(stored.Id);
        fixture.Db.AnalyticsGoals.Should().BeEmpty();
        fixture.Db.WebAnalyticsEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldReportOrderedFunnelProgressAndDropOff()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.Db.AnalyticsFunnels.Add(new AnalyticsFunnel
        {
            Name = "Pricing to customer",
            Steps =
            [
                new AnalyticsFunnelStep { Name = "Pricing", MatchType = AnalyticsGoalMatchTypes.PagePath, MatchValue = "/pricing", SortOrder = 0 },
                new AnalyticsFunnelStep { Name = "Checkout", MatchType = AnalyticsGoalMatchTypes.Event, MatchValue = "begin_checkout", SortOrder = 1 },
                new AnalyticsFunnelStep { Name = "Thank you", MatchType = AnalyticsGoalMatchTypes.PagePath, MatchValue = "/thank-you", SortOrder = 2 }
            ]
        });
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "visitor-a", "session-a", "/pricing", now.AddMinutes(-12)),
            Event("begin_checkout", "visitor-a", "session-a", "/pricing", now.AddMinutes(-11)),
            Event("pageview", "visitor-a", "session-a", "/thank-you", now.AddMinutes(-10)),
            Event("pageview", "visitor-b", "session-b", "/pricing", now.AddMinutes(-9)),
            Event("begin_checkout", "visitor-b", "session-b", "/pricing", now.AddMinutes(-8)),
            Event("begin_checkout", "visitor-c", "session-c", "/", now.AddMinutes(-7)),
            Event("pageview", "visitor-c", "session-c", "/pricing", now.AddMinutes(-6)),
            Event("pageview", "visitor-d", "session-d", "/thank-you", now.AddMinutes(-5)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db)
            .GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1));

        var funnel = dashboard.Funnels.Should().ContainSingle().Which;
        funnel.StartedSessions.Should().Be(3);
        funnel.CompletedSessions.Should().Be(1);
        funnel.CompletionRate.Should().BeApproximately(33.3m, 0.01m);
        funnel.Steps.Select(step => step.ReachedSessions).Should().Equal(3, 2, 1);
        funnel.Steps[0].DropOffSessions.Should().Be(1);
        funnel.Steps[0].DropOffRate.Should().BeApproximately(33.3m, 0.01m);
        funnel.Steps[1].DropOffSessions.Should().Be(1);
        funnel.Steps[1].DropOffRate.Should().Be(50);
    }

    [Fact]
    public async Task FunnelManagement_ShouldNormalizeReplaceStepsAndRetainAnalyticsHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new GrowthAnalyticsService(fixture.Db);
        fixture.Db.WebAnalyticsEvents.Add(
            Event("pageview", "visitor", "session", "/pricing", DateTimeOffset.UtcNow));
        await fixture.Db.SaveChangesAsync();

        await service.SaveFunnelAsync(new(
            null,
            "Lead journey",
            true,
            [
                new("Pricing", AnalyticsGoalMatchTypes.PagePath, "pricing?campaign=test"),
                new("Signup", AnalyticsGoalMatchTypes.Event, "NEWSLETTER_SIGNUP")
            ]));
        var stored = await fixture.Db.AnalyticsFunnels.Include(item => item.Steps).SingleAsync();
        stored.Steps.OrderBy(step => step.SortOrder).Select(step => step.MatchValue)
            .Should().Equal("/pricing", "newsletter_signup");

        await service.SaveFunnelAsync(new(
            stored.Id,
            "Lead journey",
            false,
            [
                new("Home", AnalyticsGoalMatchTypes.PagePath, "/"),
                new("Pricing section", AnalyticsGoalMatchTypes.PagePath, "/pricing/*"),
                new("Signup", AnalyticsGoalMatchTypes.Event, "newsletter_signup")
            ]));
        fixture.Db.ChangeTracker.Clear();
        var updated = await fixture.Db.AnalyticsFunnels.Include(item => item.Steps).SingleAsync();
        updated.IsActive.Should().BeFalse();
        updated.Steps.OrderBy(step => step.SortOrder).Select(step => step.MatchValue)
            .Should().Equal("/", "/pricing/*", "newsletter_signup");

        var invalid = () => service.SaveFunnelAsync(new(
            null,
            "Too short",
            true,
            [new("Only", AnalyticsGoalMatchTypes.PagePath, "/")]));
        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*between 2 and 8*");

        await service.DeleteFunnelAsync(updated.Id);
        fixture.Db.AnalyticsFunnels.Should().BeEmpty();
        fixture.Db.AnalyticsFunnelSteps.Should().BeEmpty();
        fixture.Db.WebAnalyticsEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldApplySavedSegmentToEntireMatchingSessions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var segment = new AnalyticsSegment
        {
            Name = "LinkedIn pricing visitors",
            Rules =
            [
                new AnalyticsSegmentRule
                {
                    Dimension = AnalyticsSegmentDimensions.Source,
                    Operator = AnalyticsSegmentOperators.Is,
                    Value = "linkedin",
                    SortOrder = 0
                },
                new AnalyticsSegmentRule
                {
                    Dimension = AnalyticsSegmentDimensions.PagePath,
                    Operator = AnalyticsSegmentOperators.StartsWith,
                    Value = "/pricing",
                    SortOrder = 1
                }
            ]
        };
        fixture.Db.AnalyticsSegments.Add(segment);
        fixture.Db.AnalyticsGoals.Add(new AnalyticsGoal
        {
            Name = "Signup",
            MatchType = AnalyticsGoalMatchTypes.Event,
            MatchValue = "newsletter_signup"
        });
        fixture.Db.AnalyticsFunnels.Add(new AnalyticsFunnel
        {
            Name = "Pricing signup",
            Steps =
            [
                new AnalyticsFunnelStep
                {
                    Name = "Pricing",
                    MatchType = AnalyticsGoalMatchTypes.PagePath,
                    MatchValue = "/pricing/*",
                    SortOrder = 0
                },
                new AnalyticsFunnelStep
                {
                    Name = "Signup",
                    MatchType = AnalyticsGoalMatchTypes.Event,
                    MatchValue = "newsletter_signup",
                    SortOrder = 1
                }
            ]
        });
        fixture.Db.WebAnalyticsEvents.AddRange(
            Event("pageview", "previous-match", "previous-match-session", "/pricing/legacy", now.AddHours(-36), source: "linkedin"),
            Event("pageview", "previous-other", "previous-other-session", "/pricing", now.AddHours(-35), source: "email"),
            Event("pageview", "visitor-a", "session-a", "/pricing/team", now.AddMinutes(-12), source: "linkedin"),
            Event("engagement", "visitor-a", "session-a", "/pricing/team", now.AddMinutes(-11), engagement: 24),
            Event("newsletter_signup", "visitor-a", "session-a", "/pricing/team", now.AddMinutes(-10)),
            Event("pageview", "visitor-b", "session-b", "/blog", now.AddMinutes(-9), source: "linkedin"),
            Event("newsletter_signup", "visitor-b", "session-b", "/blog", now.AddMinutes(-8)),
            Event("pageview", "visitor-c", "session-c", "/pricing", now.AddMinutes(-7), source: "email"),
            Event("newsletter_signup", "visitor-c", "session-c", "/pricing", now.AddMinutes(-6)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new GrowthAnalyticsService(fixture.Db)
            .GetDashboardAsync(now.AddDays(-1), now.AddMinutes(1), segment.Id);

        dashboard.Sessions.Should().Be(1);
        dashboard.Visitors.Should().Be(1);
        dashboard.PageViews.Should().Be(1);
        dashboard.AverageEngagement.Should().Be(TimeSpan.FromSeconds(24));
        dashboard.TopSources.Should().ContainSingle(row => row.Label == "linkedin");
        dashboard.VisitorsComparison.Should().Be(new AnalyticsPeriodComparison(1, 0));
        dashboard.TotalConversions.Should().Be(1);
        dashboard.OverallConversionRate.Should().Be(100);
        dashboard.Funnels.Should().ContainSingle().Which.CompletionRate.Should().Be(100);
    }

    [Fact]
    public async Task SegmentManagement_ShouldNormalizeReplaceRulesAndRetainAnalyticsHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new GrowthAnalyticsService(fixture.Db);
        fixture.Db.WebAnalyticsEvents.Add(
            Event("pageview", "visitor", "session", "/pricing", DateTimeOffset.UtcNow, source: "linkedin"));
        await fixture.Db.SaveChangesAsync();

        var id = await service.SaveSegmentAsync(new(
            null,
            "High intent",
            [
                new(AnalyticsSegmentDimensions.PagePath, AnalyticsSegmentOperators.StartsWith, "pricing?plan=team"),
                new(AnalyticsSegmentDimensions.Event, AnalyticsSegmentOperators.Is, "NEWSLETTER_SIGNUP")
            ]));
        var stored = await fixture.Db.AnalyticsSegments.Include(item => item.Rules).SingleAsync();
        stored.Id.Should().Be(id);
        stored.Rules.OrderBy(rule => rule.SortOrder).Select(rule => rule.Value)
            .Should().Equal("/pricing", "newsletter_signup");

        await service.SaveSegmentAsync(new(
            id,
            "High intent visitors",
            [new(AnalyticsSegmentDimensions.Source, AnalyticsSegmentOperators.Contains, "LinkedIn")]));
        fixture.Db.ChangeTracker.Clear();
        var updated = await fixture.Db.AnalyticsSegments.Include(item => item.Rules).SingleAsync();
        updated.Name.Should().Be("High intent visitors");
        updated.Rules.Should().ContainSingle().Which.Value.Should().Be("LinkedIn");

        var duplicate = () => service.SaveSegmentAsync(new(
            null,
            "High intent visitors",
            [new(AnalyticsSegmentDimensions.Device, AnalyticsSegmentOperators.Is, "Mobile")]));
        await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");

        await service.DeleteSegmentAsync(id);
        fixture.Db.AnalyticsSegments.Should().BeEmpty();
        fixture.Db.AnalyticsSegmentRules.Should().BeEmpty();
        fixture.Db.WebAnalyticsEvents.Should().ContainSingle();
    }

    private static WebAnalyticsEvent Event(
        string name,
        string visitor,
        string session,
        string path,
        DateTimeOffset createdAt,
        string source = "",
        string campaign = "",
        int engagement = 0,
        string countryCode = "",
        string countryName = "",
        string regionCode = "",
        string regionName = "") => new()
        {
            EventName = name,
            VisitorKey = visitor,
            SessionKey = session,
            Path = path,
            Source = source,
            Campaign = campaign,
            CountryCode = countryCode,
            CountryName = countryName,
            RegionCode = regionCode,
            RegionName = regionName,
            EngagementSeconds = engagement,
            CreatedAt = createdAt,
            OccurredAtUnixSeconds = createdAt.ToUnixTimeSeconds()
        };

    private sealed class StubGeoLocationResolver(
        AnalyticsGeoLocation? location,
        bool isConfigured = true) : IAnalyticsGeoLocationResolver
    {
        public bool IsConfigured { get; } = isConfigured;
        public IPAddress? LastAddress { get; private set; }
        public int ResolveCallCount { get; private set; }

        public AnalyticsGeoLocation? Resolve(IPAddress? address)
        {
            ResolveCallCount++;
            LastAddress = address;
            return location;
        }
    }

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
