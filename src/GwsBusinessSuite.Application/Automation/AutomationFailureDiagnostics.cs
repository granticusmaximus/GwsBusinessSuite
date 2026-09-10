using System.Linq;

namespace GwsBusinessSuite.Application.Automation;

public sealed record AutomationFailureDiagnosis(string Category, string SuggestedFix);

// Turns a raw AutomationExecution.ErrorMessage into a short category and a plain-language next
// step for the Recent Failures panel. Pure string matching against this codebase's own actual
// exception text (AutomationNodeRegistry, AutomationHttpClient, CronSchedule,
// WikiDatabasePropertyValidation) - not a guess at what an error "might" say, and not an LLM
// call: every message here is deterministic and already known at build time, so a rule engine
// answers it exactly as well as a model would, for free and instantly.
//
// Ordered most-specific-first and returns on the first match, since a message can technically
// contain more than one keyword (e.g. an access-check failure that also says "database").
public static class AutomationFailureDiagnostics
{
    private static readonly (Func<string, bool> Matches, AutomationFailureDiagnosis Diagnosis)[] Rules =
    [
        (Contains("does not have edit access", "cannot verify", "could not determine the workflow's owner"),
            new("Permissions", "The workflow's owner doesn't have Edit access to something it tried to write to (a Sentinel page, database, or CRM/CMS record). Open the target and check its sharing settings, or republish the workflow under an account that has access.")),

        (Contains("cannot be resolved", "cannot target localhost", "only http and https", "must target a public", "resolved to a private", "resolved to a reserved"),
            new("Network destination blocked", "The HTTP Request node's URL couldn't be reached - it may be mistyped, point at a private/internal address (which workflow requests can't reach for security reasons), or the host may be down. Check the URL in the node.")),

        (Contains("connection refused", "no such host", "name or service not known", "ssl connection could not be established", "an error occurred while sending the request"),
            new("Network error", "The target server refused the connection or couldn't be reached. Check that the service the HTTP Request node calls is online and the URL/port are correct.")),

        (Contains("the operation was canceled", "the operation has timed out", "timed out"),
            new("Timeout", "The request took too long and was canceled. The target service may be slow or overloaded - try again, or check whether it's experiencing an outage.")),

        (Contains("exceeded the 5 mb", "exceeds the", "safety limit"),
            new("Safety limit", "This run hit one of the automation engine's own size/count safety limits (response size, fan-out count, etc.). Reduce how much data this step processes, or split it into smaller batches.")),

        (Contains("cron expression"),
            new("Schedule", "The Schedule Trigger node's cron expression is invalid. It needs exactly 5 space-separated fields (minute hour day-of-month month day-of-week) using only *, numbers, comma lists, ranges, or /step syntax.")),

        (Contains("is required.", "must be at least", "must be at most", "doesn't match the required format"),
            new("Validation rule", "A property's validation rule (Required, a number range, or a format pattern) rejected a value this workflow tried to write. Open the target database's property settings to see the exact rule, or adjust what the workflow sends.")),

        (Contains("no longer exists", "could not find", "the row no longer exists", "the recurrence no longer exists"),
            new("Missing reference", "Something this workflow points at - a row, page, database, template, or credential - has been deleted or renamed since the workflow was published. Open the workflow and re-select the target.")),

        (Contains("recursive sub-workflow cycle", "exceeded the maximum sub-workflow call depth", "has never been published", "paused on a wait or approval node"),
            new("Workflow structure", "This is a structural problem with how workflows call each other (a cycle, too deep a chain, or a sub-workflow that isn't published/pauses on Wait). Open the Execute Workflow node and check which workflow it targets.")),

        (Contains("requires a", "requires an", "requires at least one", "requires text", "requires a title", "expected", "to be an array", "not a json object", "is not executable"),
            new("Node configuration", "A node is missing a required parameter or was given the wrong shape of data (e.g. a field expecting a list got something else). Open the node the failure points to and check its settings against what the previous node actually outputs.")),

        (Contains("not available to the automation engine"),
            new("Feature unavailable", "This node needs a service (Ollama, database access, etc.) that isn't reachable from the automation engine right now. This usually clears up on its own - if it keeps happening, it may need attention outside the workflow itself.")),
    ];

    public static AutomationFailureDiagnosis Diagnose(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return new AutomationFailureDiagnosis("Unknown", "No error message was recorded for this run. Open it in the workflow's Executions tab to see each node's input and output.");
        }

        var lowered = errorMessage.ToLowerInvariant();
        foreach (var (matches, diagnosis) in Rules)
        {
            if (matches(lowered))
            {
                return diagnosis;
            }
        }

        return new AutomationFailureDiagnosis("Unknown", "Open this run in the workflow's Executions tab to see exactly which node failed and with what input.");
    }

    private static Func<string, bool> Contains(params string[] needles) =>
        lowered => needles.Any(lowered.Contains);
}
