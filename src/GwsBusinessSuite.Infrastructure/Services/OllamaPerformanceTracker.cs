using System.Collections.Concurrent;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record OllamaPerformanceSnapshot(
    string Model,
    DateTimeOffset CompletedAt,
    double QueueWaitMilliseconds,
    double TotalMilliseconds,
    double FirstTokenMilliseconds,
    double LoadMilliseconds,
    long PromptTokens,
    double PromptMilliseconds,
    long OutputTokens,
    double TokensPerSecond);

/// <summary>
/// Retains only the latest in-process interactive timing sample per model. It contains
/// numeric operational measurements only—never prompts, responses, users, or citations.
/// </summary>
public sealed class OllamaPerformanceTracker
{
    private readonly ConcurrentDictionary<string, OllamaPerformanceSnapshot> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(OllamaPerformanceSnapshot snapshot) =>
        _latest[NormalizeModelName(snapshot.Model)] = snapshot;

    public OllamaPerformanceSnapshot? GetLatest(string model) =>
        _latest.TryGetValue(NormalizeModelName(model), out var snapshot) ? snapshot : null;

    private static string NormalizeModelName(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? model[..^":latest".Length]
            : model;
}
