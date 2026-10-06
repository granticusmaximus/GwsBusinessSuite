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
        // The "Old" deal (day -40) falls in the previous 30-day window (days -60 to -30).
        result.PreviousTotal.Should().Be(1);
    }

    [Fact]
    public async Task DrillDownAsync_ShouldReturnTheRecordsBehindTheClickedPoint()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Buyer" };
        fixture.Db.Contacts.Add(contact);
        fixture.Db.Deals.AddRange(
            Deal(contact.Id, "Lead A", DealStages.Lead, 100, Now.AddDays(-2)),
            Deal(contact.Id, "Lead B", DealStages.Lead, 250, Now.AddDays(-3)),
            Deal(contact.Id, "Won", DealStages.Won, 500, Now.AddDays(-3)),
            Deal(contact.Id, "Old lead", DealStages.Lead, 900, Now.AddDays(-45)));
        fixture.Db.CjCommissionRecords.AddRange(
            Commission("c1", "Acme", 100, 10, Now.AddDays(-1)),
            Commission("c2", "Acme", 50, 5, Now.AddDays(-2)),
            Commission("c3", "Other", 70, 7, Now.AddDays(-2)));
        await fixture.Db.SaveChangesAsync();

        var leads = await fixture.Service.DrillDownAsync(
            Editor(BiQueryShapes.Deals, BiMetrics.PipelineValue, BiDimensions.Stage), DealStages.Lead);
        leads.Select(row => row.Title).Should().Equal("Lead A", "Lead B");
        leads.Sum(row => row.Value).Should().Be(350);

        var acme = await fixture.Service.DrillDownAsync(
            Editor(BiQueryShapes.AffiliateRevenue, BiMetrics.Commission, BiDimensions.Advertiser), "Acme");
        acme.Should().HaveCount(2);
        acme.Sum(row => row.Value).Should().Be(15);
    }

    [Fact]
    public async Task PreviewAsync_ShouldReportTicketVolumeSlaBreachesAndAverageCsat()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Customer" };
        fixture.Db.Contacts.Add(contact);
        SupportTicket Ticket(string subject, string status, int? rating, bool breached, DateTimeOffset createdAt) => new()
        {
            ContactId = contact.Id, Subject = subject, Status = status, SatisfactionRating = rating,
            FirstResponseBreachNotifiedAt = breached ? createdAt.AddHours(5) : null, CreatedAt = createdAt
        };
        fixture.Db.SupportTickets.AddRange(
            Ticket("A", "Open", null, true, Now.AddDays(-1)),
            Ticket("B", "Resolved", 5, false, Now.AddDays(-2)),
            Ticket("C", "Resolved", 2, true, Now.AddDays(-3)),
            Ticket("Old", "Resolved", 1, false, Now.AddDays(-45)));
        await fixture.Db.SaveChangesAsync();

        var volume = await fixture.Service.PreviewAsync(Editor(BiQueryShapes.SupportTickets, BiMetrics.Count, BiDimensions.Status));
        volume.Total.Should().Be(3);
        volume.Points.Should().Equal(new BiDataPoint("Resolved", 2), new BiDataPoint("Open", 1));
        volume.PreviousTotal.Should().Be(1);

        (await fixture.Service.PreviewAsync(Editor(BiQueryShapes.SupportTickets, BiMetrics.SlaBreaches, BiDimensions.Status)))
            .Total.Should().Be(2);

        var csat = await fixture.Service.PreviewAsync(Editor(BiQueryShapes.SupportTickets, BiMetrics.AverageSatisfaction, BiDimensions.Status));
        csat.ValueFormat.Should().Be("Rating");
        csat.Total.Should().Be(3.5m); // the average of 5 and 2, not their sum
        csat.PreviousTotal.Should().Be(1m);

        var resolved = await fixture.Service.DrillDownAsync(
            Editor(BiQueryShapes.SupportTickets, BiMetrics.Count, BiDimensions.Status), "Resolved");
        resolved.Select(row => row.Title).Should().Equal("B", "C");
    }

    [Fact]
    public async Task MoveWidgetAsync_ShouldReorderAndEvaluateGoalsAsyncShouldReportEachCrossingOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Buyer" };
        fixture.Db.Contacts.Add(contact);
        fixture.Db.Deals.Add(Deal(contact.Id, "First", DealStages.Lead, 100, Now.AddDays(-1)));
        await fixture.Db.SaveChangesAsync();
        var first = await fixture.Service.SaveWidgetAsync("alice", new BiWidgetEditor
        {
            Title = "Pipeline", QueryShape = BiQueryShapes.Deals, Metric = BiMetrics.PipelineValue,
            Dimension = BiDimensions.Stage, Visualization = BiVisualizations.Kpi, RangeDays = 30, GoalValue = 300
        });
        var second = await fixture.Service.SaveWidgetAsync("alice", new BiWidgetEditor
        {
            Title = "Count", QueryShape = BiQueryShapes.Deals, Metric = BiMetrics.Count,
            Dimension = BiDimensions.Stage, Visualization = BiVisualizations.Bar, RangeDays = 30
        });

        await fixture.Service.MoveWidgetAsync("alice", second, -1);
        (await fixture.Service.GetDashboardAsync("alice")).Select(widget => widget.Id).Should().Equal(second, first);

        // The first check only records "below target"; nothing has crossed yet.
        (await fixture.Service.EvaluateGoalsAsync()).Should().BeEmpty();
        fixture.Db.Deals.Add(Deal(contact.Id, "Big", DealStages.Won, 500, Now.AddDays(-1)));
        await fixture.Db.SaveChangesAsync();

        var crossing = (await fixture.Service.EvaluateGoalsAsync()).Should().ContainSingle().Subject;
        crossing.Title.Should().Be("Pipeline");
        crossing.GoalMet.Should().BeTrue();
        crossing.Total.Should().Be(600);
        (await fixture.Service.EvaluateGoalsAsync()).Should().BeEmpty(); // still met: no repeat
    }

    [Fact]
    public async Task BiReportEmailService_ShouldValidateTheAddressAndEmailTheOwnersDashboard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mail = new RecordingMail();
        var reports = new GwsBusinessSuite.Infrastructure.Services.BiReportEmailService(fixture.Db, fixture.Service, mail,
            Microsoft.Extensions.Options.Options.Create(new GwsBusinessSuite.Infrastructure.Services.GrowthReportEmailOptions
            {
                Host = "smtp.example.com", FromAddress = "noreply@example.com"
            }), new FixedTimeProvider(Now));
        await fixture.Service.SaveWidgetAsync("alice", new BiWidgetEditor
        {
            Title = "Deals <b>", QueryShape = BiQueryShapes.Deals, Metric = BiMetrics.Count,
            Dimension = BiDimensions.Stage, Visualization = BiVisualizations.Kpi, RangeDays = 30, GoalValue = 5
        });

        await reports.Invoking(r => r.SaveSubscriptionAsync("alice", new BiReportSubscriptionView(true, "bob", 1, 8, null, true)))
            .Should().ThrowAsync<InvalidOperationException>();
        await reports.SaveSubscriptionAsync("alice", new BiReportSubscriptionView(true, "alice@example.com", 1, 8, null, true));

        var result = await reports.SendAsync("alice", force: true);

        result.Sent.Should().BeTrue();
        var message = mail.Sent.Should().ContainSingle().Subject;
        message.To.ToString().Should().Contain("alice@example.com");
        message.HtmlBody.Should().Contain("Deals &lt;b&gt;").And.Contain("below target");
        (await reports.GetSubscriptionAsync("alice")).LastSentAt.Should().Be(Now);
    }

    [Fact]
    public void BiReportEmailService_IsDue_ShouldSendOncePerWeekOnTheChosenDayAndHour()
    {
        var monday9 = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        var subscription = new BiReportSubscription { OwnerUsername = "a", Enabled = true, DayOfWeek = 1, HourLocal = 8 };
        GwsBusinessSuite.Infrastructure.Services.BiReportEmailService.IsDue(subscription, monday9).Should().BeTrue();
        GwsBusinessSuite.Infrastructure.Services.BiReportEmailService.IsDue(subscription, monday9.AddHours(-2)).Should().BeFalse();
        GwsBusinessSuite.Infrastructure.Services.BiReportEmailService.IsDue(subscription, monday9.AddDays(1)).Should().BeFalse();
        subscription.LastSentAt = monday9;
        GwsBusinessSuite.Infrastructure.Services.BiReportEmailService.IsDue(subscription, monday9.AddHours(3)).Should().BeFalse();
        GwsBusinessSuite.Infrastructure.Services.BiReportEmailService.IsDue(subscription, monday9.AddDays(7)).Should().BeTrue();
    }

    private sealed class RecordingMail : GwsBusinessSuite.Infrastructure.Services.IMailTransport
    {
        public List<MimeKit.MimeMessage> Sent { get; } = [];
        public GwsBusinessSuite.Infrastructure.Services.MailRoute Describe(GwsBusinessSuite.Infrastructure.Services.ISmtpTransportOptions options) =>
            new(GwsBusinessSuite.Infrastructure.Services.MailRouteKind.Smtp, "test");
        public Task SendAsync(MimeKit.MimeMessage message, GwsBusinessSuite.Infrastructure.Services.ISmtpTransportOptions options,
            string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task BiQuestionPlanner_ShouldAcceptOnlyCatalogAnswersAndFallBackToKeywords()
    {
        await using var fixture = await Fixture.CreateAsync();
        var shapes = fixture.Service.GetQueryShapes();
        var ranges = fixture.Service.GetRangeOptions();

        var parsed = BiQuestionPlanner.ParseModelAnswer(
            "Sure! {\"source\":\"Deals\",\"metric\":\"PipelineValue\",\"dimension\":\"Stage\",\"rangeDays\":100,\"title\":\"Pipeline\"}",
            shapes, ranges);
        parsed.Should().NotBeNull();
        parsed!.Metric.Should().Be(BiMetrics.PipelineValue);
        parsed.RangeDays.Should().Be(90); // snapped to the nearest allowed range
        (await fixture.Service.PreviewAsync(parsed)).MetricLabel.Should().Be("Pipeline value");

        BiQuestionPlanner.ParseModelAnswer("{\"source\":\"Users\",\"metric\":\"Count\",\"dimension\":\"Stage\"}", shapes, ranges)
            .Should().BeNull();
        BiQuestionPlanner.ParseModelAnswer("I can't help with that.", shapes, ranges).Should().BeNull();
        // Real llama3.2 output: a dropped quote on "title" must not sink the valid fields.
        var sloppy = BiQuestionPlanner.ParseModelAnswer(
            "{\"source\":\"Deals\",\"metric\":\"PipelineValue\",\"dimension\":\"Stage\",\"rangeDays\":90,\"title:\"Quarterly Pipeline\"}",
            shapes, ranges);
        sloppy.Should().NotBeNull();
        sloppy!.RangeDays.Should().Be(90);
        BiQuestionPlanner.ParseModelAnswer("{\"source\":\"Support tickets\",\"metric\":\"Average CSAT\",\"dimension\":\"Priority\"}", shapes, ranges)!
            .Metric.Should().Be(BiMetrics.AverageSatisfaction);

        var csat = BiQuestionPlanner.KeywordPlan("What's our CSAT by priority this quarter?", shapes, ranges);
        (csat.QueryShape, csat.Metric, csat.Dimension, csat.RangeDays)
            .Should().Be((BiQueryShapes.SupportTickets, BiMetrics.AverageSatisfaction, BiDimensions.Priority, 90));
        var trend = BiQuestionPlanner.KeywordPlan("blog visitors monthly trend this year", shapes, ranges);
        (trend.QueryShape, trend.Metric, trend.RangeDays).Should().Be((BiQueryShapes.ArticlePerformance, BiMetrics.Visitors, 365));
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldServeCachedResultsUntilRefreshIsRequested()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Buyer" };
        fixture.Db.Contacts.Add(contact);
        fixture.Db.Deals.Add(Deal(contact.Id, "First", DealStages.Lead, 100, Now.AddDays(-1)));
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.SaveWidgetAsync("alice", new BiWidgetEditor
        {
            Title = "Deals", QueryShape = BiQueryShapes.Deals, Metric = BiMetrics.Count,
            Dimension = BiDimensions.Stage, Visualization = BiVisualizations.Bar, RangeDays = 30
        });
        (await fixture.Service.GetDashboardAsync("alice")).Single().Chart.Total.Should().Be(1);

        fixture.Db.Deals.Add(Deal(contact.Id, "Second", DealStages.Lead, 100, Now.AddDays(-1)));
        await fixture.Db.SaveChangesAsync();

        var cached = (await fixture.Service.GetDashboardAsync("alice")).Single().Chart;
        cached.Total.Should().Be(1);
        cached.DataAsOf.Should().Be(Now);
        (await fixture.Service.GetDashboardAsync("alice", refresh: true)).Single().Chart.Total.Should().Be(2);
    }

    [Fact]
    public async Task GetDashboardAsync_ShouldApplyACustomRangeAndCompareTheEquallyLongPeriodBefore()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Buyer" };
        fixture.Db.Contacts.Add(contact);
        fixture.Db.Deals.AddRange(
            Deal(contact.Id, "In range", DealStages.Lead, 100, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero)),
            Deal(contact.Id, "In range 2", DealStages.Won, 200, new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero)),
            Deal(contact.Id, "Previous", DealStages.Won, 50, new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero)),
            Deal(contact.Id, "Recent", DealStages.Lead, 999, Now.AddDays(-1)));
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.SaveWidgetAsync("alice", new BiWidgetEditor
        {
            Title = "Pipeline", QueryShape = BiQueryShapes.Deals, Metric = BiMetrics.PipelineValue,
            Dimension = BiDimensions.Stage, Visualization = BiVisualizations.Bar, RangeDays = 30
        });

        var range = new BiDateRange(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        var chart = (await fixture.Service.GetDashboardAsync("alice", range)).Single().Chart;

        chart.Total.Should().Be(300);
        chart.PreviousTotal.Should().Be(50);
        chart.From.Should().Be(range.From);
        await fixture.Invoking(f => f.Service.GetDashboardAsync("alice", new BiDateRange(range.To, range.From)))
            .Should().ThrowAsync<InvalidOperationException>();
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
        CreatedAt = createdAt,
        CreatedAtUnixSeconds = createdAt.ToUnixTimeSeconds()
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
            Service = new BusinessIntelligenceService(db, new FixedTimeProvider(Now),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
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
