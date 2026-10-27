using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// The shell's status strip (tech-forward T4).
public sealed class ShellStatusTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ApplicationDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task JobsRunning_ShouldCountLiveExecutionsOnly_NotCrashedOrFinishedOnes()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var workflow = new AutomationWorkflow { Name = "Nightly digest", CreatedBy = "u" };
            db.AutomationWorkflows.Add(workflow);
            await db.SaveChangesAsync();
            AutomationExecution Execution(string status, long? heartbeat) => new()
            {
                WorkflowId = workflow.Id, Status = status, HeartbeatAtUnixSeconds = heartbeat, StartedAtUnixSeconds = Now.AddHours(-2).ToUnixTimeSeconds(), CreatedBy = "u"
            };
            db.AutomationExecutions.AddRange(
                Execution(AutomationExecutionStatuses.Running, Now.AddMinutes(-1).ToUnixTimeSeconds()),
                Execution(AutomationExecutionStatuses.Running, Now.AddHours(-1).ToUnixTimeSeconds()),
                Execution(AutomationExecutionStatuses.Succeeded, Now.ToUnixTimeSeconds()),
                Execution(AutomationExecutionStatuses.Waiting, Now.ToUnixTimeSeconds()));
            await db.SaveChangesAsync();
        }
        var status = new ShellStatusService(_factory, new OllamaWorkloadScheduler(), new BuildStamp("abc1234", Now.AddHours(-3)), new Clock(Now));

        var shown = await status.GetAsync();

        shown.JobsRunning.Should().Be(1, "the hour-quiet one crashed (past the 15-minute orphan threshold), and finished or waiting ones aren't running");
        shown.AiBusy.Should().BeFalse();
        shown.Commit.Should().Be("abc1234");
        shown.BuiltAtMs.Should().Be(Now.AddHours(-3).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task AiActivity_ShouldFollowTheSchedulersLease_AndItsQueue()
    {
        var scheduler = new OllamaWorkloadScheduler();
        scheduler.Activity.Should().Be((false, 0));

        var first = await scheduler.AcquireAsync();
        var queued = scheduler.AcquireAsync(OllamaWorkloadPriority.Background).AsTask();
        scheduler.Activity.Should().Be((true, 1));

        await first.DisposeAsync();
        var second = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Activity.Should().Be((true, 0), "the queued generation took the model straight away");
        await second.DisposeAsync();
        scheduler.Activity.Should().Be((false, 0));
    }

    [Theory]
    [InlineData("1.0.0+c2fc30f1b2d3e4f5", "c2fc30f")]
    [InlineData("1.0.0+abc", "abc")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0+", null)]
    [InlineData("", null)]
    public void CommitFrom_ShouldShortenTheShaTheSdkAppends(string informational, string? expected) =>
        BuildStamp.CommitFrom(informational).Should().Be(expected);

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
