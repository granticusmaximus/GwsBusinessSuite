using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Simple "when ... then ..." rules on a Sentinel database, like Notion's database automations.
// A rule isn't stored separately: it's an ordinary Automation workflow built from the rule
// (database.rowChangedTrigger + If/Date & Time + one action), published and switched on. The
// workflow's description starts with RuleMarker so the database's Automations panel can find
// its rules, and anything a rule can't express can still be edited in Automation.
public static class SentinelDatabaseRuleKinds
{
    public const string WhenRowAdded = "rowAdded";
    public const string WhenPropertyEquals = "propertyEquals";

    public const string ThenSetProperty = "setProperty";
    public const string ThenEmail = "email";
    public const string ThenRunWorkflow = "runWorkflow";
}

public sealed record SentinelDatabaseRule(
    Guid WikiDatabaseId,
    string When,
    Guid? WhenPropertyId,
    string? WhenValue,
    string Then,
    Guid? SetPropertyId = null,
    string? SetValue = null,
    // Date properties: write the moment the rule runs instead of SetValue.
    bool SetToNow = false,
    // Leave a value someone already filled in alone, e.g. keep the first "Completed" date.
    bool OnlyIfEmpty = false,
    string? EmailTo = null,
    Guid? RunWorkflowId = null);

public sealed record SentinelDatabaseRuleView(Guid WorkflowId, string Summary, string Status);

public sealed record SentinelDatabaseRuleGraph(
    string Name,
    string Description,
    IReadOnlyList<AutomationNodeView> Nodes,
    IReadOnlyList<AutomationConnectionView> Connections);

public interface ISentinelDatabaseRuleService
{
    Task<IReadOnlyList<SentinelDatabaseRuleView>> ListAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);

    // Admins only, since a rule is an Automation workflow.
    Task<SentinelDatabaseRuleView> CreateAsync(SentinelDatabaseRule rule, string performedBy, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid wikiDatabaseId, Guid workflowId, string performedBy, CancellationToken cancellationToken = default);
}

public static class SentinelDatabaseRuleBuilder
{
    public const string RuleMarker = "sentinel-database-rule:";

    public static string MarkerFor(Guid wikiDatabaseId) => RuleMarker + wikiDatabaseId;

    // The rule's one-line summary is the second line of the workflow description.
    public static string? SummaryFromDescription(string description, Guid wikiDatabaseId)
    {
        var marker = MarkerFor(wikiDatabaseId);
        if (!description.StartsWith(marker, StringComparison.Ordinal)) return null;
        var summary = description[marker.Length..].Trim();
        return summary.Length == 0 ? "Database rule" : summary;
    }

    // Plain-language summary, e.g. "When Status is Done, set Completed to now (only if empty)".
    public static string Describe(SentinelDatabaseRule rule, WikiDatabase database, string? workflowName = null)
    {
        string PropertyName(Guid? id) => database.Properties.FirstOrDefault(property => property.Id == id)?.Name ?? "a property";
        string ValueLabel(Guid? propertyId, string? value)
        {
            var property = database.Properties.FirstOrDefault(item => item.Id == propertyId);
            var option = property is null ? null : WikiDatabasePropertyConfig.GetOptions(property).FirstOrDefault(item => item.Id == value);
            return option?.Label ?? (string.IsNullOrEmpty(value) ? "empty" : value);
        }

        var when = rule.When == SentinelDatabaseRuleKinds.WhenRowAdded
            ? "When a row is added"
            : $"When {PropertyName(rule.WhenPropertyId)} is {ValueLabel(rule.WhenPropertyId, rule.WhenValue)}";
        var then = rule.Then switch
        {
            SentinelDatabaseRuleKinds.ThenSetProperty =>
                $"set {PropertyName(rule.SetPropertyId)} to {(rule.SetToNow ? "now" : ValueLabel(rule.SetPropertyId, rule.SetValue))}"
                + (rule.OnlyIfEmpty ? " (only if empty)" : string.Empty),
            SentinelDatabaseRuleKinds.ThenEmail => $"email {rule.EmailTo}",
            SentinelDatabaseRuleKinds.ThenRunWorkflow => $"run {workflowName ?? "a workflow"}",
            _ => "do nothing"
        };
        return $"{when}, {then}";
    }

    // Checks the rule against the database; returns a message for the person, or null if fine.
    public static string? Validate(SentinelDatabaseRule rule, WikiDatabase database)
    {
        WikiDatabaseProperty? Find(Guid? id) => database.Properties.FirstOrDefault(property => property.Id == id);

        if (rule.When == SentinelDatabaseRuleKinds.WhenPropertyEquals)
        {
            if (Find(rule.WhenPropertyId) is not { } whenProperty) return "Choose the property the rule watches.";
            if (!CanWatch(whenProperty)) return $"Rules can't watch a {whenProperty.Type} property.";
        }
        else if (rule.When != SentinelDatabaseRuleKinds.WhenRowAdded)
        {
            return "Choose when the rule runs.";
        }

        switch (rule.Then)
        {
            case SentinelDatabaseRuleKinds.ThenSetProperty:
                if (Find(rule.SetPropertyId) is not { } target) return "Choose the property to set.";
                if (!CanSet(target)) return $"Rules can't set a {target.Type} property.";
                if (rule.SetToNow && target.Type != WikiDatabasePropertyTypes.Date) return "\"Now\" only works for a Date property.";
                break;
            case SentinelDatabaseRuleKinds.ThenEmail:
                if (string.IsNullOrWhiteSpace(rule.EmailTo) || !rule.EmailTo.Contains('@')) return "Enter the email address to notify.";
                break;
            case SentinelDatabaseRuleKinds.ThenRunWorkflow:
                if (rule.RunWorkflowId is null) return "Choose the workflow to run.";
                break;
            default:
                return "Choose what the rule does.";
        }
        return null;
    }

    // Matched by the trigger's equals condition against the stored value (option id or text).
    public static bool CanWatch(WikiDatabaseProperty property) => property.Type is
        WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status or WikiDatabasePropertyTypes.Checkbox
        or WikiDatabasePropertyTypes.Text or WikiDatabasePropertyTypes.Number or WikiDatabasePropertyTypes.Url
        or WikiDatabasePropertyTypes.Email or WikiDatabasePropertyTypes.Phone;

    // What database.setRowProperty (SaveInlineCellAsync) accepts.
    public static bool CanSet(WikiDatabaseProperty property) => property.Type is
        WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status or WikiDatabasePropertyTypes.Checkbox
        or WikiDatabasePropertyTypes.Text or WikiDatabasePropertyTypes.Number or WikiDatabasePropertyTypes.Url
        or WikiDatabasePropertyTypes.Email or WikiDatabasePropertyTypes.Phone or WikiDatabasePropertyTypes.Date;

    public static SentinelDatabaseRuleGraph Build(SentinelDatabaseRule rule, WikiDatabase database, string? workflowName = null)
    {
        var summary = Describe(rule, database, workflowName);
        var nodes = new List<AutomationNodeView>();
        var connections = new List<AutomationConnectionView>();
        AutomationNodeView? previous = null;
        var previousOutput = "main";

        void Add(string name, string typeKey, JsonObject parameters, string output = "main")
        {
            var node = new AutomationNodeView(Guid.NewGuid(), name, typeKey, 1, 120 + nodes.Count * 260, 200,
                parameters.ToJsonString(), null, false, false, false, 1, 0, 0, string.Empty);
            if (previous is not null)
            {
                connections.Add(new AutomationConnectionView(Guid.NewGuid(), previous.Id, previousOutput, node.Id, "main"));
            }
            nodes.Add(node);
            previous = node;
            previousOutput = output;
        }

        var conditions = new JsonArray();
        if (rule.When == SentinelDatabaseRuleKinds.WhenPropertyEquals)
        {
            conditions.Add(new JsonObject
            {
                ["propertyId"] = rule.WhenPropertyId.ToString(),
                ["operator"] = "equals",
                ["value"] = rule.WhenValue ?? string.Empty
            });
        }
        Add("Row changed", "database.rowChangedTrigger", new JsonObject
        {
            ["wikiDatabaseId"] = database.Id.ToString(),
            ["conditions"] = conditions
        });

        if (rule.When == SentinelDatabaseRuleKinds.WhenRowAdded)
        {
            Add("Only new rows", "core.if", new JsonObject
            {
                ["left"] = "{{ $json.isNew }}",
                ["operator"] = "equals",
                ["right"] = "true"
            }, output: "true");
        }

        switch (rule.Then)
        {
            case SentinelDatabaseRuleKinds.ThenSetProperty:
                if (rule.OnlyIfEmpty)
                {
                    // "exists" is true when the value is filled in; the false branch continues.
                    Add("Only if empty", "core.if", new JsonObject
                    {
                        ["left"] = $"{{{{ $json.values.{rule.SetPropertyId} }}}}",
                        ["operator"] = "exists",
                        ["right"] = string.Empty
                    }, output: "false");
                }
                if (rule.SetToNow)
                {
                    Add("Now", "core.dateTime", new JsonObject { ["outputField"] = "timestamp" });
                }
                Add("Set property", "database.setRowProperty", new JsonObject
                {
                    ["wikiDatabaseId"] = database.Id.ToString(),
                    ["rowId"] = "{{ $json.rowId }}",
                    ["propertyId"] = rule.SetPropertyId.ToString(),
                    ["value"] = rule.SetToNow ? "{{ $json.timestamp.iso }}" : rule.SetValue ?? string.Empty
                });
                break;
            case SentinelDatabaseRuleKinds.ThenEmail:
                Add("Email", "core.notify", new JsonObject
                {
                    ["to"] = rule.EmailTo!.Trim(),
                    ["subject"] = $"Sentinel: {database.Title}",
                    ["message"] = $"A row in \"{database.Title}\" matched a rule: {summary}.\n\nRow id: {{{{ $json.rowId }}}}"
                });
                break;
            case SentinelDatabaseRuleKinds.ThenRunWorkflow:
                Add("Run workflow", "automation.subWorkflow", new JsonObject { ["workflowId"] = rule.RunWorkflowId.ToString() });
                break;
        }

        var name = $"{database.Title}: {summary}";
        return new SentinelDatabaseRuleGraph(
            name.Length > 120 ? name[..117] + "..." : name,
            $"{MarkerFor(database.Id)}\n{summary}",
            nodes,
            connections);
    }
}
