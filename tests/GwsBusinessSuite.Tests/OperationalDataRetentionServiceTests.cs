using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class OperationalDataRetentionServiceTests
{
    [Fact]
    public async Task PurgeExpiredRecordsAsync_ShouldDeleteOnlyTerminalAutomationExecutionsPastTheirCutoff_AndTheirNodeChildren()
    {
        // Regression guard: a Waiting/Running execution must never be purged regardless of
        // age, and node-execution children must be removed alongside their parent (SQLite FK
        // cascades aren't guaranteed to be enabled for this app's connections).
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var workflow = new AutomationWorkflow { Name = "wf" };
        fixture.Db.AutomationWorkflows.Add(workflow);

        var expiredSucceeded = NewExecution(workflow.Id, AutomationExecutionStatuses.Succeeded, now.AddDays(-100));
        var expiredRunning = NewExecution(workflow.Id, AutomationExecutionStatuses.Running, null);
        expiredRunning.FinishedAt = null;
        expiredRunning.FinishedAtUnixSeconds = null;
        var recentSucceeded = NewExecution(workflow.Id, AutomationExecutionStatuses.Succeeded, now.AddDays(-1));

        fixture.Db.AutomationExecutions.AddRange(expiredSucceeded, expiredRunning, recentSucceeded);
        fixture.Db.AutomationNodeExecutions.Add(new AutomationNodeExecution
        {
            ExecutionId = expiredSucceeded.Id, NodeId = Guid.NewGuid(), StartedAt = now.AddDays(-100)
        });
        await fixture.Db.SaveChangesAsync();

        var deleted = await fixture.Service.PurgeExpiredRecordsAsync();

        deleted.Should().Be(1);
        (await fixture.Db.AutomationExecutions.CountAsync()).Should().Be(2);
        (await fixture.Db.AutomationExecutions.AnyAsync(x => x.Id == expiredSucceeded.Id)).Should().BeFalse();
        (await fixture.Db.AutomationExecutions.AnyAsync(x => x.Id == expiredRunning.Id)).Should().BeTrue("a Running execution must never be purged regardless of age");
        (await fixture.Db.AutomationExecutions.AnyAsync(x => x.Id == recentSucceeded.Id)).Should().BeTrue();
        (await fixture.Db.AutomationNodeExecutions.CountAsync()).Should().Be(0, "child node executions must be purged with their parent");
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_ShouldPurgeExpiredRowsAcrossEveryOtherTrackedTable_AndLeaveRecentRowsAlone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        var site = new CmsSite { Name = "Site", Slug = "site" };
        var appGenRequest = new AppGenerationRequest { TargetSiteId = site.Id, Title = "req" };
        var podcastShow = new PodcastShow { Title = "show" };
        var podcastEpisode = new PodcastEpisode { PodcastShowId = podcastShow.Id };
        fixture.Db.CmsSites.Add(site);
        fixture.Db.AppGenerationRequests.Add(appGenRequest);
        fixture.Db.PodcastShows.Add(podcastShow);
        fixture.Db.PodcastEpisodes.Add(podcastEpisode);

        fixture.Db.SocialPostAlerts.Add(new SocialPostAlert { SocialPostId = Guid.NewGuid(), CreatedAt = now.AddDays(-200) });
        fixture.Db.SocialPostAlerts.Add(new SocialPostAlert { SocialPostId = Guid.NewGuid(), CreatedAt = now.AddDays(-1) });

        fixture.Db.AppGenerationMessages.Add(new AppGenerationMessage { AppGenerationRequestId = appGenRequest.Id, Content = "old", CreatedAt = now.AddDays(-400) });
        fixture.Db.AppGenerationMessages.Add(new AppGenerationMessage { AppGenerationRequestId = appGenRequest.Id, Content = "new", CreatedAt = now.AddDays(-1) });

        // FetchedAtUnixSeconds is overwritten from FetchedAt by
        // ApplicationDbContext.SynchronizeNewsItemTimestamps on save, so set FetchedAt itself.
        fixture.Db.NewsItems.Add(new NewsItem { Title = "old", Url = "https://a", FetchedAt = now.AddDays(-60) });
        fixture.Db.NewsItems.Add(new NewsItem { Title = "new", Url = "https://b", FetchedAt = now.AddDays(-1) });

        fixture.Db.CjCommissionRecords.Add(new CjCommissionRecord { ExternalId = "old", CreatedAtUnixSeconds = now.AddYears(-4).ToUnixTimeSeconds() });
        fixture.Db.CjCommissionRecords.Add(new CjCommissionRecord { ExternalId = "new", CreatedAtUnixSeconds = now.AddDays(-1).ToUnixTimeSeconds() });

        fixture.Db.PodcastListenProgresses.Add(new PodcastListenProgress { EpisodeId = podcastEpisode.Id, Username = "grant", LastPlayedAt = now.AddDays(-400) });
        fixture.Db.PodcastListenProgresses.Add(new PodcastListenProgress { EpisodeId = podcastEpisode.Id, Username = "grant-2", LastPlayedAt = now.AddDays(-1) });

        await fixture.Db.SaveChangesAsync();

        var deleted = await fixture.Service.PurgeExpiredRecordsAsync();

        deleted.Should().Be(5);
        (await fixture.Db.SocialPostAlerts.CountAsync()).Should().Be(1);
        (await fixture.Db.AppGenerationMessages.CountAsync()).Should().Be(1);
        (await fixture.Db.NewsItems.CountAsync()).Should().Be(1);
        (await fixture.Db.CjCommissionRecords.CountAsync()).Should().Be(1);
        (await fixture.Db.PodcastListenProgresses.CountAsync()).Should().Be(1);
        // SQLite/EF Core can't translate a DateTimeOffset range comparison server-side -
        // materialize then filter client-side.
        (await fixture.Db.SocialPostAlerts.ToListAsync()).Should().ContainSingle(x => x.CreatedAt > now.AddDays(-30));
        (await fixture.Db.CjCommissionRecords.AnyAsync(x => x.ExternalId == "new")).Should().BeTrue();
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_ShouldReturnZero_WhenNothingIsExpired()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.NewsItems.Add(new NewsItem { Title = "fresh", Url = "https://a", FetchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        await fixture.Db.SaveChangesAsync();

        var deleted = await fixture.Service.PurgeExpiredRecordsAsync();

        deleted.Should().Be(0);
        (await fixture.Db.NewsItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_ShouldDeleteExpiredLiveShowRecordingRows_AndTheirBackingFiles_ButLeaveRecentOnesAlone()
    {
        var recordingsRoot = Path.Combine(Path.GetTempPath(), $"gws-live-show-recordings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recordingsRoot);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var expiredFileName = "expired.webm";
            var recentFileName = "recent.webm";
            await File.WriteAllTextAsync(Path.Combine(recordingsRoot, expiredFileName), "old");
            await File.WriteAllTextAsync(Path.Combine(recordingsRoot, recentFileName), "new");

            await using var fixture = await Fixture.CreateAsync(recordingsRoot);
            var session = new LiveShowSession { Title = "show", InviteToken = "token" };
            fixture.Db.LiveShowSessions.Add(session);
            fixture.Db.LiveShowRecordings.Add(new LiveShowRecording { SessionId = session.Id, FileName = expiredFileName, CreatedAt = now.AddDays(-365) });
            fixture.Db.LiveShowRecordings.Add(new LiveShowRecording { SessionId = session.Id, FileName = recentFileName, CreatedAt = now.AddDays(-1) });
            await fixture.Db.SaveChangesAsync();

            var deleted = await fixture.Service.PurgeExpiredRecordsAsync();

            deleted.Should().Be(1);
            (await fixture.Db.LiveShowRecordings.CountAsync()).Should().Be(1);
            (await fixture.Db.LiveShowRecordings.AnyAsync(x => x.FileName == recentFileName)).Should().BeTrue();
            File.Exists(Path.Combine(recordingsRoot, expiredFileName)).Should().BeFalse("the expired recording's backing file must be removed, not just its row");
            File.Exists(Path.Combine(recordingsRoot, recentFileName)).Should().BeTrue("a recording still inside its retention window must never lose its file");
        }
        finally
        {
            Directory.Delete(recordingsRoot, recursive: true);
        }
    }

    private static AutomationExecution NewExecution(Guid workflowId, string status, DateTimeOffset? finishedAt) => new()
    {
        WorkflowId = workflowId,
        Status = status,
        FinishedAt = finishedAt,
        FinishedAtUnixSeconds = finishedAt?.ToUnixTimeSeconds()
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db, OperationalDataRetentionService service)
        { _connection = connection; Db = db; Service = service; }
        public ApplicationDbContext Db { get; }
        public OperationalDataRetentionService Service { get; }

        public static async Task<Fixture> CreateAsync(string? liveShowRecordingsPath = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options); await db.Database.EnsureCreatedAsync();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(liveShowRecordingsPath is null
                    ? []
                    : new Dictionary<string, string?> { ["LiveShow:RecordingsPath"] = liveShowRecordingsPath })
                .Build();
            var service = new OperationalDataRetentionService(
                db, Options.Create(new OperationalDataRetentionOptions()), configuration,
                NullLogger<OperationalDataRetentionService>.Instance);
            return new(connection, db, service);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
