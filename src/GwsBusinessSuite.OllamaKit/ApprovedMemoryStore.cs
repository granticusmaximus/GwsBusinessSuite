using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.OllamaKit;

public sealed record ApprovedExchange(string Question, string Answer, DateTimeOffset ApprovedAt);

// Local counterpart to the hosted app's "teach SentinelGPT from this answer" memory
// (SentinelAiService.BuildApprovedMemoryContextAsync) - a thumbs-up on a native chat answer
// appends it here; a later related question's context gets it injected back in. One JSON file
// (a flat list, not one-per-exchange - this is expected to stay small) under a caller-supplied
// directory, scoped across all local conversations rather than just the current one, matching
// the hosted feature's own cross-conversation scope.
public sealed class ApprovedMemoryStore(string filePath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "and", "are", "can", "could", "does", "for",
        "from", "have", "into", "just", "latest", "more", "much", "please", "show", "that",
        "the", "their", "then", "there", "these", "this", "what", "when", "where", "which",
        "with", "would", "you", "your"
    };

    public async Task AppendAsync(string question, string answer, CancellationToken cancellationToken)
    {
        var exchanges = (await LoadAllAsync(cancellationToken)).ToList();
        exchanges.Add(new ApprovedExchange(question, answer, DateTimeOffset.UtcNow));

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(exchanges, SerializerOptions), cancellationToken);
        File.Move(tempPath, filePath, overwrite: true);
    }

    // Mirrors BuildApprovedMemoryContextAsync's scoring: term-overlap against question+answer,
    // score > 0 required (no "just show the most recent" fallback - an unrelated approved answer
    // is worse than none), top 3 by score then recency.
    public async Task<string> BuildContextAsync(string instruction, CancellationToken cancellationToken)
    {
        var terms = SearchTerms(instruction);
        if (terms.Length == 0) return string.Empty;

        var exchanges = await LoadAllAsync(cancellationToken);
        var lessons = exchanges
            .Select(exchange => new
            {
                Exchange = exchange,
                Score = terms.Count(term =>
                    exchange.Question.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || exchange.Answer.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Exchange.ApprovedAt)
            .Take(3)
            .ToList();
        if (lessons.Count == 0) return string.Empty;

        var memory = new System.Text.StringBuilder(
            "APPROVED SENTINELGPT MEMORY - reuse as guidance, but re-verify current facts:\n");
        foreach (var lesson in lessons)
        {
            memory.AppendLine($"PRIOR QUESTION: {Limit(lesson.Exchange.Question, 1_000)}");
            memory.AppendLine($"APPROVED ANSWER: {Limit(lesson.Exchange.Answer, 2_000)}");
        }
        return memory.ToString();
    }

    private async Task<IReadOnlyList<ApprovedExchange>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ApprovedExchange>>(
                await File.ReadAllTextAsync(filePath, cancellationToken), SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string[] SearchTerms(string instruction) =>
        Regex.Matches(instruction.ToLowerInvariant(), "[a-z0-9][a-z0-9._-]{2,}")
            .Select(match => match.Value)
            .Where(term => !StopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
