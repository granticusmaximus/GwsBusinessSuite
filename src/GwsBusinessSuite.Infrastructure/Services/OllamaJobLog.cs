namespace GwsBusinessSuite.Infrastructure.Services;

// One AI job as the failover service ran it (19-11-26, the AI health page): which machine, how long,
// and how it ended. Numbers and names only: never a prompt, a reply or a user.
public sealed record OllamaJob(DateTimeOffset At, string Operation, string Model, string Machine, double Milliseconds, string Outcome, string? Error);

public static class OllamaJobOutcomes
{
    public const string Answered = "answered";
    // The Mac couldn't answer, so the droplet ran it again.
    public const string HandedOver = "handed to the droplet";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class OllamaMachines
{
    public const string Mac = "Mac";
    public const string Droplet = "AI droplet";
}

public sealed record OllamaMachineSummary(string Machine, int Jobs, int Failed, int HandedOver, double? MedianMilliseconds);

// The last 500 jobs, in memory (a restart starts over).
public sealed class OllamaJobLog(TimeProvider? time = null)
{
    public const int Capacity = 500;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Queue<OllamaJob> _jobs = new();

    public void Record(string operation, string model, string machine, TimeSpan took, string outcome, Exception? error = null)
    {
        var job = new OllamaJob(_time.GetUtcNow(), operation, model, machine, took.TotalMilliseconds, outcome,
            error is null ? null : $"{error.GetType().Name}: {Shorten(error.Message)}");
        lock (_gate)
        {
            _jobs.Enqueue(job);
            while (_jobs.Count > Capacity) _jobs.Dequeue();
        }
    }

    public IReadOnlyList<OllamaJob> Recent(int count = 50)
    {
        lock (_gate) return _jobs.Reverse().Take(count).ToList();
    }

    public IReadOnlyList<OllamaMachineSummary> Summary(TimeSpan window)
    {
        var since = _time.GetUtcNow() - window;
        List<OllamaJob> jobs;
        lock (_gate) jobs = _jobs.Where(j => j.At >= since).ToList();
        return new[] { OllamaMachines.Mac, OllamaMachines.Droplet }.Select(machine =>
        {
            var mine = jobs.Where(j => j.Machine == machine).ToList();
            var answered = mine.Where(j => j.Outcome == OllamaJobOutcomes.Answered).Select(j => j.Milliseconds).Order().ToList();
            return new OllamaMachineSummary(machine, mine.Count, mine.Count(j => j.Outcome == OllamaJobOutcomes.Failed),
                mine.Count(j => j.Outcome == OllamaJobOutcomes.HandedOver), answered.Count == 0 ? null : answered[answered.Count / 2]);
        }).ToList();
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..159] + "…";
}

// What the AI health page warns about first (19-11-26): the Mac down for 15 minutes or more, or five
// or more jobs failing or handed over in the last 10 minutes.
public static class AiHealthWarnings
{
    public static readonly TimeSpan MacDownFor = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TroubleWindow = TimeSpan.FromMinutes(10);
    public const int TroubleJobs = 5;

    public static string? For(bool macUp, DateTimeOffset? macChangedAt, IReadOnlyList<OllamaJob> jobs, DateTimeOffset now)
    {
        if (!macUp && macChangedAt is { } down && now - down >= MacDownFor)
            return $"The Mac hasn't answered for {(int)(now - down).TotalMinutes} minutes, so the AI droplet is doing everything (slowly). Check the Mac is awake, Ollama is running and Tailscale is connected.";
        var trouble = jobs.Count(j => now - j.At <= TroubleWindow && j.Outcome is OllamaJobOutcomes.Failed or OllamaJobOutcomes.HandedOver);
        return trouble >= TroubleJobs
            ? $"{trouble} jobs failed or were handed over in the last {(int)TroubleWindow.TotalMinutes} minutes. The latest are listed below."
            : null;
    }
}
