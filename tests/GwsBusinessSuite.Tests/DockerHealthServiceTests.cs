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

    // ── ListContainersAsync / GetContainerDetailsAsync graceful degradation ──
    // No real Docker daemon is available in tests, so these exercise the
    // "socket not reachable" fallback path rather than real container data.

    [Fact]
    public async Task ListContainersAsync_ShouldReportUnavailable_WhenNoDockerSocket()
    {
        await using var db = await CreateDbAsync();
        var service = new DockerHealthService(db);

        var result = await service.ListContainersAsync();

        result.Available.Should().BeFalse();
        result.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

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
