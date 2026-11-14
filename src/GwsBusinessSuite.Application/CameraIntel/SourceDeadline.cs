using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Overwatch layers that ask many sources at once (cameras, incidents) used to wait for the slowest:
// one source hanging until its 30-second timeout held the whole layer for 30 s, and the layer drew
// nothing until then (production 13/14-11-26: incidents and city traffic at 30.0 s, cameras 5-12 s).
// Now the layer takes what has arrived by the deadline, in the sources' own order. A late source
// keeps going in the background, so its answer (each source caches its own) is there next time.
public static class SourceDeadline
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(6);

    public static async Task<IReadOnlyList<IReadOnlyList<T>>> GatherAsync<T>(
        IReadOnlyList<(string Source, Task<IReadOnlyList<T>> Load)> loads, TimeSpan deadline, ILogger logger, string what,
        CancellationToken cancellationToken = default)
    {
        if (loads.Count == 0) return [];
        var all = Task.WhenAll(loads.Select(load => load.Load));
        var finished = await Task.WhenAny(all, Task.Delay(deadline, cancellationToken));
        if (finished != all)
        {
            var late = loads.Where(load => !load.Load.IsCompleted).Select(load => load.Source).ToList();
            if (late.Count > 0)
                logger.LogInformation("Overwatch {What}: {Late} still loading after {Seconds:0} s; showing the other sources now",
                    what, string.Join(", ", late), deadline.TotalSeconds);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return loads.Select(load => load.Load.IsCompletedSuccessfully ? load.Load.Result : []).ToList();
    }
}
