using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace GwsBusinessSuite.Web.Services;

// Evidence for the "Rejoining the server..." popup (reported 2026-10-09 as roughly every 10
// minutes, in both the browser and the Mac app). The popup means the browser heard nothing from
// the server for 30 s (SignalR's default server timeout) or the socket closed. That's either the
// server freezing or the connection being cut somewhere in between (Cloudflare tunnel, network),
// and the two need different fixes - these log lines tell them apart:
//   - "Circuit connection down/up" - every drop and reconnect, with how long it was down.
//   - "Server stall" (ServerStallMonitor) - the server itself was frozen for several seconds.
// A drop with no stall just before it points at the network/tunnel; a stall right before it
// points at the server (thread-pool starvation, a long GC pause, the host swapping).
// Search the durable logs (/app/data/logs) for "Circuit connection" and "Server stall".
public sealed class CircuitDiagnosticsHandler(ILogger<CircuitDiagnosticsHandler> logger) : CircuitHandler
{
    private static readonly ConcurrentDictionary<string, long> DownSince = new();

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        DownSince[circuit.Id] = Stopwatch.GetTimestamp();
        logger.LogInformation("Circuit connection down: {CircuitId}", Short(circuit.Id));
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (DownSince.TryRemove(circuit.Id, out var since))
        {
            logger.LogInformation("Circuit connection up again after {DownSeconds:0.0} s: {CircuitId}",
                Stopwatch.GetElapsedTime(since).TotalSeconds, Short(circuit.Id));
        }
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        DownSince.TryRemove(circuit.Id, out _);
        return Task.CompletedTask;
    }

    // Enough to correlate a down with its up without writing full circuit ids to the log.
    private static string Short(string circuitId) => circuitId.Length <= 8 ? circuitId : circuitId[..8];
}

// A 1-second timer that notices when it fires late. Late by several seconds means nothing on the
// server could run - the same condition that stops SignalR keep-alives and makes browsers show
// "Rejoining". Logs what the runtime looked like at the time, so a stall can be traced to thread
// pool starvation (many queued work items), garbage collection (pause time jumped), or something
// outside the process (neither - e.g. the droplet swapping or the container being throttled).
public sealed class ServerStallMonitor(ILogger<ServerStallMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportOver = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gcPauseBefore = GC.GetTotalPauseDuration();
        var gen2Before = GC.CollectionCount(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var late = Stopwatch.GetElapsedTime(started) - Tick;
            var gcPause = GC.GetTotalPauseDuration();
            var gen2 = GC.CollectionCount(2);
            if (late > ReportOver)
            {
                logger.LogWarning(
                    "Server stall: a 1 s timer fired {LateSeconds:0.0} s late. Thread pool: {Threads} threads, {Queued} queued work items. " +
                    "GC: {Gen2} gen2 collections and {PauseMs:0} ms of pauses during the stall. Managed heap {HeapMb:0} MB.",
                    late.TotalSeconds, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                    gen2 - gen2Before, (gcPause - gcPauseBefore).TotalMilliseconds, GC.GetTotalMemory(false) / 1024d / 1024d);
            }
            gcPauseBefore = gcPause;
            gen2Before = gen2;
        }
    }
}
