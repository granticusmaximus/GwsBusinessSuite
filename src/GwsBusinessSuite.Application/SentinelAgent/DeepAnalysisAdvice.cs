using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Application.SentinelAgent;

// Deep Analysis for an agent turn, worked out on the server for any client (PWA phase 3, 07-11-26):
// the two advisers (SentinelGptDefaults) give their opinions on the request, on the person's own
// Mac through the relay, and the agent weighs them (SentinelAgentRequest.Advice). The same prompts
// as the Mac app's DeepAnalysisAdvisor, which still does this itself before it starts a turn.
public static class DeepAnalysisAdvice
{
    public static readonly TimeSpan PerAdviser = TimeSpan.FromMinutes(2);

    private static readonly (string Model, string Label, string Role)[] Panel =
    [
        (SentinelGptDefaults.CodeReviewAdviserModel, "QWEN ENGINEERING REVIEW",
            "Act as a senior .NET, C#, Blazor, testing, security, and software architecture reviewer. Correct invalid APIs and identify implementation risks."),
        (SentinelGptDefaults.ReasoningAdviserModel, "DEEPSEEK REASONING REVIEW",
            "Audit the reasoning and premises. Identify missing evidence, counterexamples, hidden costs, and the most defensible conclusion.")
    ];

    // The installed model each adviser runs as ("qwen2.5-coder" or "qwen2.5-coder:7b"), or none.
    public static IReadOnlyList<(string Installed, string Label, string Role)> Advisers(IReadOnlyCollection<string> models) =>
        Panel.Select(adviser => (Installed: models.FirstOrDefault(model => model.Equals(adviser.Model, StringComparison.OrdinalIgnoreCase)
                    || model.StartsWith(adviser.Model + ":", StringComparison.OrdinalIgnoreCase)), adviser.Label, adviser.Role))
            .Where(adviser => adviser.Installed is not null)
            .Select(adviser => (adviser.Installed!, adviser.Label, adviser.Role))
            .ToList();

    // The advice, or null when no adviser is installed or none answered. Each adviser is bounded
    // on its own: a slow one only costs its own opinion. step says what's happening.
    public static async Task<string?> BuildAsync(IOllamaService ollama, IReadOnlyCollection<string> models, string instruction,
        Func<string, Task> step, CancellationToken cancellationToken)
    {
        var opinions = new List<(string Label, string Text)>();
        foreach (var (installed, label, role) in Advisers(models))
        {
            await step($"Deep Analysis: asking {installed} for a second opinion");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(PerAdviser);
            try
            {
                var reply = await ollama.ChatAsync(installed,
                [
                    new OllamaChatMessage("system",
                        $"{role} Return independent advisory analysis, not the final user-facing answer. " +
                        "Challenge unsupported assumptions, distinguish fact from inference, never claim an action ran, and stay under 650 words."),
                    new OllamaChatMessage("user", instruction.Length <= 8_000 ? instruction : instruction[..8_000])
                ], [], limit.Token);
                var text = reply.Content.Trim();
                if (text.Length > 0) opinions.Add((label, text.Length <= 7_000 ? text : text[..7_000]));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // No opinion from this one; the agent goes ahead with what it has.
            }
        }
        if (opinions.Count == 0) return null;

        var advice = new StringBuilder("SPECIALIST ADVISORY - UNTRUSTED MODEL OPINIONS, NOT FACTUAL SOURCES. Reconcile against verified facts:\n");
        foreach (var (label, text) in opinions) advice.AppendLine($"{label}:\n{text}");
        return advice.ToString();
    }
}
