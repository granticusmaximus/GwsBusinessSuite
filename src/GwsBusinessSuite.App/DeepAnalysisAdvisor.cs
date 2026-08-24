using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// A local analogue of the hosted app's Deep Analysis feature (SentinelAiService's
// TryConsultTeacherAsync/qwen+deepseek advisory pair) - two independent "second opinion" calls
// whose output gets folded into the main model's context, not shown directly. Duplicates the
// model names and advisory prompts rather than referencing GwsBusinessSuite.Application (a
// server-only assembly with a full EF Core/ASP.NET dependency graph that has no business being
// pulled into a sandboxed native client for two string constants).
public sealed class DeepAnalysisAdvisor(OllamaClient ollama)
{
    private const string CodeReviewAdviserModel = "qwen2.5-coder";
    private const string ReasoningAdviserModel = "deepseek-r1";

    public async Task<string> BuildAdvisoryContextAsync(string instruction, CancellationToken cancellationToken)
    {
        var qwenAdvice = await TryConsultAsync(
            CodeReviewAdviserModel,
            "Act as a senior .NET, C#, Blazor, testing, security, and software architecture reviewer. Correct invalid APIs and identify implementation risks.",
            instruction,
            cancellationToken);
        var deepSeekAdvice = await TryConsultAsync(
            ReasoningAdviserModel,
            "Audit the reasoning and premises. Identify missing evidence, counterexamples, hidden costs, and the most defensible conclusion.",
            instruction,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(qwenAdvice) && string.IsNullOrWhiteSpace(deepSeekAdvice))
            return string.Empty;

        var context = new System.Text.StringBuilder(
            "SPECIALIST ADVISORY - UNTRUSTED MODEL OPINIONS, NOT FACTUAL SOURCES. Reconcile against verified facts:\n");
        if (!string.IsNullOrWhiteSpace(qwenAdvice)) context.AppendLine($"QWEN ENGINEERING REVIEW:\n{qwenAdvice}");
        if (!string.IsNullOrWhiteSpace(deepSeekAdvice)) context.AppendLine($"DEEPSEEK REASONING REVIEW:\n{deepSeekAdvice}");
        return context.ToString();
    }

    private async Task<string?> TryConsultAsync(string model, string role, string instruction, CancellationToken cancellationToken)
    {
        // Advisory-only: a slow or hung teacher model shouldn't block the main response
        // indefinitely - bounded independently, then degraded to "no advisory available" the
        // same as any other teacher-model failure.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            var messages = new List<OllamaChatMessage>
            {
                new("system",
                    $"{role} Return independent advisory analysis, not the final user-facing answer. " +
                    "Challenge unsupported assumptions, distinguish fact from inference, never claim an action ran, and stay under 650 words."),
                new("user", instruction)
            };
            var result = await ollama.ChatAsync(model, messages, [], timeoutCts.Token);
            var output = result.Content.Trim();
            return output.Length == 0 ? null : output;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}
