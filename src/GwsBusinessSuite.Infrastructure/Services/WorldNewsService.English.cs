using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed partial class WorldNewsService
{
    private readonly SemaphoreSlim _englishRunning = new(1, 1);
    private readonly OllamaWorkloadScheduler? _workloads;
    private DateTimeOffset _lastEnglishListInvalidation;

    public string EnglishStatus { get; private set; } = "English translations and report summaries are queued.";

    public async Task<bool> ProcessEnglishAsync(CancellationToken cancellationToken)
    {
        if (!await _englishRunning.WaitAsync(0, cancellationToken)) return false;
        try
        {
            Store.EnsureEnglishQueueIndex();
            // Reserve part of every pass for old reports so a busy feed cannot starve the archive.
            var now = DateTimeOffset.UtcNow;
            var pending = Store.ReportsDueEnglish(now, HeadlineTranslator.ReportsPerCall - 2)
                .Concat(Store.ReportsDueEnglish(now, 2, oldestFirst: true))
                .DistinctBy(article => article.Id).ToList();
            if (pending.Count == 0)
            {
                EnglishStatus = "Waiting for incoming reports or scheduled English retries.";
                return false;
            }
            EnglishStatus = $"Preparing English headlines, descriptions and summaries for {pending.Count} reports.";
            Store.DeferEnglishReports(pending.Select(article => article.Id), now.AddMinutes(15));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var standAside = _workloads is null ? CancellationTokenSource.CreateLinkedTokenSource(timeout.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _workloads.InteractiveArrivedToken);
            using var background = _workloads?.UseBackgroundPriority();
            using var shared = MacRelayScope.ForSharedWork();
            await using var scope = _scopes.CreateAsyncScope();
            var raw = await scope.ServiceProvider.GetRequiredService<IOllamaService>().GenerateAsync(
                _model, HeadlineTranslator.ReportSystemPrompt, HeadlineTranslator.ReportPrompt(pending), 16_384, standAside.Token);
            var translations = HeadlineTranslator.ParseReports(raw, pending);
            var saved = Store.SaveEnglishReports(translations, DateTimeOffset.UtcNow);
            if (translations.Count != pending.Count)
                _logger.LogWarning("World News English pass returned {Returned} of {Expected} reports; missing reports will retry",
                    translations.Count, pending.Count);
            if (saved != translations.Count)
                _logger.LogInformation("World News English pass discarded {Count} removed or changed reports", translations.Count - saved);
            EnglishStatus = $"Prepared {saved} English reports this pass; incoming news and archive processing continue." +
                (translations.Count < pending.Count ? " Missing results are scheduled for retry." : "");
            if (saved > 0 && DateTimeOffset.UtcNow - _lastEnglishListInvalidation >= TimeSpan.FromMinutes(1))
            {
                ClearLists(rebuild: false);
                _mapCache?.Clear();
                _lastEnglishListInvalidation = DateTimeOffset.UtcNow;
            }
            return saved > 0;
        }
        catch (ServerAiUnavailableException)
        {
            EnglishStatus = "English processing is waiting for the configured AI service or an available admin Mac.";
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            EnglishStatus = "English processing paused for interactive AI work or a timeout; reports will retry.";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or SqliteException)
        {
            EnglishStatus = "English processing failed; reports remain pending and will retry automatically.";
            _logger.LogWarning(ex, "World News English report processing failed; the backlog will retry");
            return false;
        }
        finally
        {
            _englishRunning.Release();
            Changed?.Invoke();
        }
    }
}
