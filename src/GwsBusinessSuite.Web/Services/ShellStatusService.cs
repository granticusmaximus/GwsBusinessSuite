using System.Reflection;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Web.Services;

// What the shell's status strip shows (tech-forward T4). BuiltAtMs is Unix milliseconds, for app.js.
public sealed record ShellStatus(int JobsRunning, bool AiBusy, int AiWaiting, string? Commit, long BuiltAtMs);

// The build this server runs: when its files were written (in the Docker image, the deploy's
// build) and the commit, when the build had git to read it from. The image is built without .git,
// so production shows the time alone.
public sealed record BuildStamp(string? Commit, DateTimeOffset BuiltAt)
{
    public static BuildStamp For(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var builtAt = string.IsNullOrEmpty(assembly.Location)
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(File.GetLastWriteTimeUtc(assembly.Location), TimeSpan.Zero);
        return new BuildStamp(CommitFrom(informational), builtAt);
    }

    // The SDK appends "+<commit sha>" to the informational version when it can read one; this is
    // its first seven characters, as git shows it.
    public static string? CommitFrom(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus == informationalVersion.Length - 1) return null;
        var sha = informationalVersion[(plus + 1)..];
        return sha[..Math.Min(7, sha.Length)];
    }
}

// A DbContext per call from the factory: the strip polls this from every open admin tab.
public sealed class ShellStatusService(IDbContextFactory<ApplicationDbContext> dbFactory, OllamaWorkloadScheduler ollama, BuildStamp build, TimeProvider time)
{
    public async Task<ShellStatus> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // A Running execution whose heartbeat has been quiet past the orphan threshold crashed
        // rather than ran on (the resume sweep picks it up), so it isn't counted as running.
        var cutoff = time.GetUtcNow().ToUnixTimeSeconds() - AutomationExecutionService.OrphanThresholdSeconds;
        var running = await db.AutomationExecutions.CountAsync(execution => execution.Status == AutomationExecutionStatuses.Running
            && (execution.HeartbeatAtUnixSeconds ?? execution.StartedAtUnixSeconds ?? 0) >= cutoff, ct);
        var (busy, waiting) = ollama.Activity;
        return new ShellStatus(running, busy, waiting, build.Commit, build.BuiltAt.ToUnixTimeMilliseconds());
    }
}
