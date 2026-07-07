using FluentAssertions;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class DockerHealthServiceTests
{
    // ── Suggested tip lookup ────────────────────────────────────

    [Fact]
    public void SuggestTip_ShouldFlagOutOfMemory_ForExitCode137()
    {
        var view = new ContainerHealthView { ExitCode = 137 };

        var tip = DockerHealthService.SuggestTip(view);

        tip.Should().Contain("OOM", "OOM-killed containers exit with code 137");
    }

    [Fact]
    public void SuggestTip_ShouldFlagHealthcheckFailure_WhenUnhealthy()
    {
        var view = new ContainerHealthView { Health = "unhealthy" };

        var tip = DockerHealthService.SuggestTip(view);

        tip.Should().Contain("healthcheck");
    }

    [Fact]
    public void SuggestTip_ShouldFlagCrashLoop_WhenRestartCountIsHigh()
    {
        var view = new ContainerHealthView { RestartCount = 7 };

        var tip = DockerHealthService.SuggestTip(view);

        tip.Should().Contain("crash loop");
    }

    [Fact]
    public void SuggestTip_ShouldFlagGenericCrash_ForNonZeroExitCode()
    {
        var view = new ContainerHealthView { ExitCode = 1 };

        var tip = DockerHealthService.SuggestTip(view);

        tip.Should().Contain("1");
    }

    [Fact]
    public void SuggestTip_ShouldFallBackToLogTail_WhenNoKnownPatternMatches()
    {
        var view = new ContainerHealthView { ExitCode = 0, Health = "none", RestartCount = 0 };

        var tip = DockerHealthService.SuggestTip(view);

        tip.Should().Contain("log tail");
    }

    // ── Alert list / mark-read / count ───────────────────────────

    [Fact]
    public async Task ListAlertsAsync_ShouldReturnMostRecentFirst()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);

        db.DockerHealthAlerts.Add(NewAlert("gwssuite", "First", DateTimeOffset.UtcNow.AddMinutes(-10)));
        db.DockerHealthAlerts.Add(NewAlert("ollama", "Second", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var alerts = await service.ListAlertsAsync(unreadOnly: false);

        alerts.Should().HaveCount(2);
        alerts[0].Message.Should().Be("Second");
    }

    [Fact]
    public async Task ListAlertsAsync_ShouldFilterToUnreadOnly_WhenRequested()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);

        var readAlert = NewAlert("gwssuite", "Already read", DateTimeOffset.UtcNow.AddMinutes(-5));
        readAlert.IsRead = true;
        db.DockerHealthAlerts.Add(readAlert);
        db.DockerHealthAlerts.Add(NewAlert("ollama", "Still unread", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var unread = await service.ListAlertsAsync(unreadOnly: true);

        unread.Should().ContainSingle(a => a.Message == "Still unread");
    }

    [Fact]
    public async Task MarkAlertReadAsync_ShouldSetIsReadToTrue()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);
        var alert = NewAlert("gwssuite", "Needs attention", DateTimeOffset.UtcNow);
        db.DockerHealthAlerts.Add(alert);
        await db.SaveChangesAsync();

        await service.MarkAlertReadAsync(alert.Id);

        (await service.ListAlertsAsync(unreadOnly: false)).Single().IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task CountUnreadAlertsAsync_ShouldOnlyCountUnread()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);

        var read = NewAlert("gwssuite", "Read", DateTimeOffset.UtcNow);
        read.IsRead = true;
        db.DockerHealthAlerts.Add(read);
        db.DockerHealthAlerts.Add(NewAlert("ollama", "Unread 1", DateTimeOffset.UtcNow));
        db.DockerHealthAlerts.Add(NewAlert("ollama", "Unread 2", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        (await service.CountUnreadAlertsAsync()).Should().Be(2);
    }

    // ── Action logging ───────────────────────────────────────────

    [Fact]
    public async Task ListActionLogsAsync_ShouldReturnMostRecentFirst_FilteredByContainer()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);

        db.DockerActionLogs.Add(NewLog("gwssuite", "Start", DateTimeOffset.UtcNow.AddMinutes(-5)));
        db.DockerActionLogs.Add(NewLog("gwssuite", "Restart", DateTimeOffset.UtcNow));
        db.DockerActionLogs.Add(NewLog("ollama", "Stop", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var logs = await service.ListActionLogsAsync("gwssuite");

        logs.Should().HaveCount(2);
        logs[0].Action.Should().Be("Restart");
    }

    [Fact]
    public async Task ListActionLogsAsync_ShouldReturnAllContainers_WhenNoFilterGiven()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);

        db.DockerActionLogs.Add(NewLog("gwssuite", "Start", DateTimeOffset.UtcNow));
        db.DockerActionLogs.Add(NewLog("ollama", "Stop", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var logs = await service.ListActionLogsAsync(null);

        logs.Should().HaveCount(2);
    }

    private static DockerActionLog NewLog(string containerName, string action, DateTimeOffset createdAt) => new()
    {
        ContainerName = containerName,
        Action = action,
        Succeeded = true,
        ResultSummary = $"{action} completed.",
        PerformedBy = "test",
        CreatedAt = createdAt,
        CreatedBy = "test"
    };

    // Start/Stop/Restart/Remove/Pull/Recreate/ExecCommandAsync themselves aren't covered
    // here either, for the same reason as ListContainersAsync/GetContainerDetailsAsync
    // below - they require a real Docker socket. RunActionAsync's audit-logging plumbing
    // (success and failure paths) is exercised indirectly by DigitalOceanServiceTests,
    // which shares the same DockerActionLog write path for droplet-level actions.

    // ListContainersAsync/GetContainerDetailsAsync themselves aren't covered here -
    // whether a Docker socket is reachable varies by environment (GitHub Actions
    // runners have a real one; a plain local `dotnet test` usually doesn't), so
    // asserting either outcome would be flaky. The graceful-degradation behavior is
    // already verified manually against local dev (no socket) and production
    // (real socket) - see the plan's verification notes.

    private static DockerHealthAlert NewAlert(string containerName, string message, DateTimeOffset createdAt) => new()
    {
        ContainerName = containerName,
        Severity = DockerHealthAlertSeverity.Error,
        Message = message,
        CreatedAt = createdAt,
        CreatedBy = "test"
    };

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
