using System.Threading.Channels;
using GwsBusinessSuite.Application.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace GwsBusinessSuite.Web.Diagnostics;

// Feeds warnings and errors to the issues store (logging plan, Phase 1, 12-11-26). Emit runs on
// whatever thread logged, so it only queues; LogIssueIngestService writes in batches. A full
// queue drops the oldest: losing a sighting beats slowing every caller that logs.
public sealed class LogIssueSink : ILogEventSink
{
    private static readonly AsyncLocal<bool> Suppressed = new();
    private readonly Channel<CapturedLogEvent> _queue = Channel.CreateBounded<CapturedLogEvent>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<CapturedLogEvent> Reader => _queue.Reader;

    // While the store saves, its own lines (EF warnings, a failed write) go to the files only,
    // or one failure would feed itself.
    public static IDisposable Suppress()
    {
        Suppressed.Value = true;
        return new Restore();
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning || Suppressed.Value) return;
        _queue.Writer.TryWrite(Capture(logEvent));
    }

    public static CapturedLogEvent Capture(LogEvent logEvent)
    {
        var source = Text(logEvent, "SourceContext") ?? "-";
        var client = source == DiagnosticsEndpointExtensions.ClientErrorCategory;
        var exception = logEvent.Exception?.ToString();
        return new CapturedLogEvent(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            Text(logEvent, "Origin") ?? "server",
            source,
            logEvent.MessageTemplate.Text,
            Cap(LogRedactor.Redact(logEvent.RenderMessage()), 2000)!,
            logEvent.Exception?.GetType().FullName,
            Cap(LogRedactor.Redact(exception), 8000),
            // A client report is about the page it came from, not the request that carried it.
            client ? Text(logEvent, "ClientTraceId") ?? logEvent.TraceId?.ToHexString() : logEvent.TraceId?.ToHexString(),
            Text(logEvent, "UserName"),
            client ? Text(logEvent, "ClientPage") ?? Text(logEvent, "RequestPath") : Text(logEvent, "RequestPath"),
            Text(logEvent, "CircuitId"),
            Text(logEvent, "Release") ?? "unknown",
            client ? Text(logEvent, "ClientMessage") : null,
            client ? Text(logEvent, "ClientStack") is { Length: > 0 } stack ? stack : null : null);
    }

    private static string? Text(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text } ? text : null;

    private static string? Cap(string? value, int maximum) =>
        value is null ? null : value.Length <= maximum ? value : value[..maximum];

    private sealed class Restore : IDisposable
    {
        public void Dispose() => Suppressed.Value = false;
    }
}

public sealed class LogIssueIngestService(LogIssueSink sink, IServiceScopeFactory scopes, TimeProvider time, ILogger<LogIssueIngestService> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = LogIssueSink.Suppress();
        var lastPrune = DateTimeOffset.MinValue;
        var batch = new List<CapturedLogEvent>(BatchSize);
        try
        {
            while (await sink.Reader.WaitToReadAsync(stoppingToken))
            {
                // Gather a short window's worth, so a burst becomes one write.
                await Task.Delay(BatchWindow, time, stoppingToken);
                while (batch.Count < BatchSize && sink.Reader.TryRead(out var captured)) batch.Add(captured);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<ILogIssueService>();
                    await store.RecordAsync(batch, stoppingToken);
                    if (time.GetUtcNow() - lastPrune > PruneEvery)
                    {
                        lastPrune = time.GetUtcNow();
                        await store.PruneAsync(lastPrune, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not record {Count} log events in the issues store", batch.Count);
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
