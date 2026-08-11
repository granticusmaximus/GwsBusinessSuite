using FluentAssertions;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class OperationalAlertServiceTests
{
    [Fact]
    public async Task NotifyFailureAsync_ShouldWriteAnEmailToThePickupDirectory_WhenConfigured()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gws-operational-alert-{Guid.NewGuid():N}");
        try
        {
            var service = CreateService(directory, out _);

            await service.NotifyFailureAsync("database-backup", "Backup failed", new InvalidOperationException("disk full"));

            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.eml") : [];
            files.Should().ContainSingle();
            var content = await File.ReadAllTextAsync(files[0]);
            content.Should().Contain("database-backup").And.Contain("Backup failed").And.Contain("disk full");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NotifyFailureAsync_ShouldThrottleRepeatedAlertsForTheSameSource_WithinTheCooldownWindow()
    {
        // Regression guard: a persistently failing background job must page once per cooldown
        // window, not once per tick - otherwise a stuck job spams the inbox forever.
        var directory = Path.Combine(Path.GetTempPath(), $"gws-operational-alert-{Guid.NewGuid():N}");
        try
        {
            var service = CreateService(directory, out _);

            await service.NotifyFailureAsync("automation-resume-sweep", "First failure");
            await service.NotifyFailureAsync("automation-resume-sweep", "Second failure");

            var files = Directory.GetFiles(directory, "*.eml");
            files.Should().ContainSingle("the second call within the cooldown window must be suppressed");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NotifyFailureAsync_ShouldNotThrottleAcrossDifferentSources()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gws-operational-alert-{Guid.NewGuid():N}");
        try
        {
            var service = CreateService(directory, out _);

            await service.NotifyFailureAsync("database-backup", "Backup failed");
            await service.NotifyFailureAsync("automation-resume-sweep", "Automation failed");

            var files = Directory.GetFiles(directory, "*.eml");
            files.Should().HaveCount(2, "the cooldown key is per-source, not global");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NotifyFailureAsync_ShouldDoNothing_WhenNoNotifyEmailIsConfigured()
    {
        // Alerting is opt-in - an admin who never set a recipient must see no attempted send,
        // not an exception, and definitely not a pickup-directory write.
        var directory = Path.Combine(Path.GetTempPath(), $"gws-operational-alert-{Guid.NewGuid():N}");
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new OperationalAlertService(
            Options.Create(new OperationalAlertOptions { NotifyEmail = "" }),
            Options.Create(new GrowthReportEmailOptions { FromAddress = "alerts@gws.test", PickupDirectory = directory }),
            cache,
            NullLogger<OperationalAlertService>.Instance);

        await service.NotifyFailureAsync("database-backup", "Backup failed");

        Directory.Exists(directory).Should().BeFalse("no email should ever be attempted without a configured recipient");
    }

    [Fact]
    public async Task NotifyFailureAsync_ShouldNeverThrow_WhenSendingFails()
    {
        // Alerting itself must never break the caller's own failure-handling path.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new OperationalAlertService(
            Options.Create(new OperationalAlertOptions { NotifyEmail = "ops@gws.test" }),
            Options.Create(new GrowthReportEmailOptions { FromAddress = "alerts@gws.test", Host = "invalid.invalid.example", Port = 1 }),
            cache,
            NullLogger<OperationalAlertService>.Instance);

        var act = async () => await service.NotifyFailureAsync("database-backup", "Backup failed");

        await act.Should().NotThrowAsync();
    }

    private static OperationalAlertService CreateService(string pickupDirectory, out MemoryCache cache)
    {
        cache = new MemoryCache(new MemoryCacheOptions());
        return new OperationalAlertService(
            Options.Create(new OperationalAlertOptions { NotifyEmail = "ops@gws.test", CooldownMinutes = 60 }),
            Options.Create(new GrowthReportEmailOptions { FromAddress = "alerts@gws.test", PickupDirectory = pickupDirectory }),
            cache,
            NullLogger<OperationalAlertService>.Instance);
    }
}
