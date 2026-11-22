using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.SentinelAgent;

// A model can describe an action it never took. The application decides whether a change is
// complete from the persisted action views produced by successful tool calls.
public static class SentinelAgentCompletionVerifier
{
    private static readonly string[] MutationWords =
    [
        "add", "archive", "change", "complete", "create", "delete", "draft", "edit", "forget",
        "log", "make", "move", "publish", "remember", "rename", "reply", "reschedule", "run",
        "send", "set", "start", "stop", "trash", "turn", "update"
    ];

    public const string UnverifiedMessage =
        "I couldn't verify that any requested change was made, so I haven't marked this complete.";

    public static string Verify(string request, string modelAnswer, IReadOnlyList<SentinelAgentActionView> actions)
    {
        var changed = actions.Where(action => action.Status == SentinelAgentActionStatuses.Done
            && action.Tool != SentinelAgentRunner.PlanToolName).ToList();
        var waiting = actions.Where(action => action.Status == SentinelAgentActionStatuses.AwaitingConfirm).ToList();

        if (waiting.Count > 0 || changed.Count > 0) return modelAnswer;
        return RequestsMutation(request) && ClaimsCompletion(modelAnswer) ? UnverifiedMessage : modelAnswer;
    }

    public static bool RequestsMutation(string request)
    {
        var words = request.Split([' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || request.TrimStart().StartsWith("how ", StringComparison.OrdinalIgnoreCase)) return false;
        return words.Any(word => MutationWords.Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ClaimsCompletion(string answer)
    {
        string[] claims = ["added", "completed", "created", "deleted", "done", "drafted", "made", "moved", "published", "renamed", "sent", "updated"];
        var words = answer.Split([' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word => claims.Contains(word, StringComparer.OrdinalIgnoreCase));
    }
}
