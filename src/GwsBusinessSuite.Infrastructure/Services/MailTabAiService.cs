using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// SentinelGPT decides the inbox tabs MailSorter wasn't sure of (Mail tabs phase 3, 21-11-26). A batch at a
// time: the decision is saved as "ai" and the stored subject is cleared, since it was kept only for this.
// Unclear rows nobody decided in UnclearKeepDays are dropped (the message has usually left the inbox).
public sealed class MailTabAiService(IAppDbContext db, IOllamaService ollama, IOptions<ContentStudioOptions> options, TimeProvider time,
    MailInboxCache? inboxCache = null, ILogger<MailTabAiService>? logger = null)
{
    public const int UnclearKeepDays = 14;

    // Returns how many messages were decided.
    public async Task<int> DecideAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-UnclearKeepDays);
        var stale = (await db.MailMessageTabs.Where(row => row.Source == MailTabSources.Unclear).ToListAsync(cancellationToken))
            .Where(row => row.CreatedAt < cutoff).ToList();
        if (stale.Count > 0)
        {
            db.MailMessageTabs.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Materialized, then ordered (SQLite can't ORDER BY a DateTimeOffset).
        var batch = (await db.MailMessageTabs.Where(row => row.Source == MailTabSources.Unclear).ToListAsync(cancellationToken))
            .OrderBy(row => row.CreatedAt).Take(MailTabAi.BatchSize).ToList();
        if (batch.Count == 0) return 0;

        var raw = await ollama.GenerateAsync(options.Value.Model, MailTabAi.SystemPrompt,
            MailTabAi.Prompt(batch.Select(row => new MailTabAi.Pending(row.Sender, row.Subject, row.Reason)).ToList()), cancellationToken);
        var decided = MailTabAi.Parse(raw, batch.Count);
        var now = time.GetUtcNow();
        foreach (var (number, tabKey) in decided)
        {
            var row = batch[number - 1];
            row.TabKey = tabKey;
            row.Source = MailTabSources.Ai;
            row.Reason = "SentinelGPT sorted it";
            row.Subject = string.Empty;
            row.UpdatedAt = now;
            row.UpdatedBy = "sentinelgpt";
        }
        if (decided.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var accountId in batch.Select(row => row.AccountId).Distinct()) inboxCache?.Invalidate(accountId);
        }
        if (decided.Count < batch.Count)
            logger?.LogInformation("Mail tabs: SentinelGPT placed {Decided} of {Count} unclear messages; the rest are asked again later", decided.Count, batch.Count);
        return decided.Count;
    }
}

// Every two minutes, at background priority, while there's unclear mail. The AI being unavailable (no
// Mac and no AI droplet) just means waiting.
public sealed class MailTabAiBackgroundService(IServiceScopeFactory scopeFactory, OllamaWorkloadScheduler workloads, ILogger<MailTabAiBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var priority = workloads.UseBackgroundPriority();
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MailTabAiService>().DecideAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (ServerAiUnavailableException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mail tabs: SentinelGPT couldn't sort unclear mail this round; it tries again in two minutes");
            }
        }
    }
}
