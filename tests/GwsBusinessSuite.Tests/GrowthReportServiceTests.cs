using FluentAssertions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class GrowthReportServiceTests
{
    [Fact]
    public async Task SaveScheduleAsync_ShouldValidateAndCalculateNextWeeklyDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero); // Sunday
        var service = CreateService(fixture.Db, new CapturingSender(), now);

        var id = await service.SaveScheduleAsync(new(
            null, " Weekly summary ", "owner@example.com", AnalyticsReportFrequencies.Weekly,
            7, (int)DayOfWeek.Monday, 13, true));

        var schedule = await fixture.Db.AnalyticsReportSchedules.SingleAsync();
        schedule.Id.Should().Be(id);
        schedule.Name.Should().Be("Weekly summary");
        schedule.NextRunAtUnixSeconds.Should().Be(
            new DateTimeOffset(2026, 8, 3, 13, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());

        var invalid = () => service.SaveScheduleAsync(new(
            null, "Bad", "not-an-email", AnalyticsReportFrequencies.Monthly, 30, 31, 9, true));
        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*valid recipient*");
    }

    [Fact]
    public async Task SendNowAsync_ShouldRenderAndRecordDeliveredAnalyticsEmail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        fixture.Db.WebAnalyticsEvents.Add(new WebAnalyticsEvent
        {
            EventName = WebAnalyticsEventNames.PageView,
            VisitorKey = "visitor",
            SessionKey = "session",
            Path = "/pricing",
            PageTitle = "Pricing",
            Source = "newsletter",
            OccurredAtUnixSeconds = now.AddHours(-1).ToUnixTimeSeconds(),
            CreatedAt = now.AddHours(-1)
        });
        fixture.Db.AnalyticsAnnotations.Add(new AnalyticsAnnotation
        {
            OccurredOnUnixSeconds = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            Note = "Launch <script>alert('x')</script>"
        });
        await fixture.Db.SaveChangesAsync();
        var sender = new CapturingSender();
        var service = CreateService(fixture.Db, sender, now);
        var id = await service.SaveScheduleAsync(new(
            null, "Executive growth", "owner@example.com", AnalyticsReportFrequencies.Weekly,
            7, (int)DayOfWeek.Monday, 13, true));
        var originalNext = (await fixture.Db.AnalyticsReportSchedules.SingleAsync()).NextRunAtUnixSeconds;

        await service.SendNowAsync(id);

        sender.Messages.Should().ContainSingle();
        var message = sender.Messages.Single();
        message.RecipientEmail.Should().Be("owner@example.com");
        message.Subject.Should().Contain("Executive growth");
        message.PlainTextBody.Should().Contain("Unique visitors: 1").And.Contain("/pricing");
        message.HtmlBody.Should().Contain("Launch &lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;")
            .And.NotContain("Launch <script>");
        fixture.Db.ChangeTracker.Clear();
        var delivered = await fixture.Db.AnalyticsReportSchedules.SingleAsync();
        delivered.LastStatus.Should().Be(AnalyticsReportDeliveryStatuses.Delivered);
        delivered.LastDeliveredAt.Should().Be(now);
        delivered.NextRunAtUnixSeconds.Should().Be(originalNext);
    }

    [Fact]
    public async Task DeliverDueAsync_ShouldRetryFailuresThenAdvanceSuccessfulSchedule()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var sender = new CapturingSender { Failure = new InvalidOperationException("SMTP unavailable") };
        var service = CreateService(fixture.Db, sender, now);
        var id = await service.SaveScheduleAsync(new(
            null, "Weekly", "owner@example.com", AnalyticsReportFrequencies.Weekly,
            7, (int)DayOfWeek.Monday, 13, true));
        var schedule = await fixture.Db.AnalyticsReportSchedules.SingleAsync();
        schedule.NextRunAtUnixSeconds = now.AddMinutes(-1).ToUnixTimeSeconds();
        await fixture.Db.SaveChangesAsync();

        (await service.DeliverDueAsync()).Should().Be(0);
        fixture.Db.ChangeTracker.Clear();
        schedule = await fixture.Db.AnalyticsReportSchedules.SingleAsync();
        schedule.LastStatus.Should().Be(AnalyticsReportDeliveryStatuses.Failed);
        schedule.LastError.Should().Be("SMTP unavailable");
        schedule.NextRunAtUnixSeconds.Should().Be(now.AddMinutes(15).ToUnixTimeSeconds());

        sender.Failure = null;
        schedule.NextRunAtUnixSeconds = now.AddMinutes(-1).ToUnixTimeSeconds();
        await fixture.Db.SaveChangesAsync();
        (await service.DeliverDueAsync()).Should().Be(1);
        fixture.Db.ChangeTracker.Clear();
        schedule = await fixture.Db.AnalyticsReportSchedules.SingleAsync();
        schedule.LastStatus.Should().Be(AnalyticsReportDeliveryStatuses.Delivered);
        schedule.LastError.Should().BeEmpty();
        schedule.NextRunAtUnixSeconds.Should().BeGreaterThan(now.ToUnixTimeSeconds());
        schedule.Id.Should().Be(id);
    }

    [Fact]
    public async Task PickupSender_ShouldWriteInspectableMimeMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gws-growth-email-{Guid.NewGuid():N}");
        try
        {
            var sender = new GrowthReportEmailSender(Options.Create(new GrowthReportEmailOptions
            {
                FromAddress = "reports@gws.test",
                PickupDirectory = directory
            }));

            await sender.SendAsync(new(
                "owner@example.com", "Weekly Growth", "Plain report", "<h1>HTML report</h1>"));

            var file = Directory.GetFiles(directory, "*.eml").Should().ContainSingle().Subject;
            var message = await File.ReadAllTextAsync(file);
            message.Should().Contain("To: owner@example.com")
                .And.Contain("Subject: Weekly Growth")
                .And.Contain("Plain report")
                .And.Contain("HTML report");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static GrowthReportService CreateService(
        ApplicationDbContext db,
        IGrowthReportEmailSender sender,
        DateTimeOffset now) => new(
            db,
            new GrowthAnalyticsService(db),
            sender,
            Options.Create(new GrowthReportEmailOptions
            {
                DashboardUrl = "https://admin.example.test/admin/growth"
            }),
            new FixedTimeProvider(now),
            NullLogger<GrowthReportService>.Instance);

    private sealed class CapturingSender : IGrowthReportEmailSender
    {
        public GrowthReportDeliveryConfiguration Configuration { get; } = new(true, "Test transport ready.");
        public List<GrowthReportEmail> Messages { get; } = [];
        public Exception? Failure { get; set; }

        public Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            Messages.Add(email);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
