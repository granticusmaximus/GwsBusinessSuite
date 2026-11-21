using System.Text.Json;

namespace GwsBusinessSuite.Application.Mail;

// Mail rules (Mail phase 6, 21-11-26, Grant: "set up folders and mail rules to have certain emails to
// those folders"). A rule: conditions on a new inbox message's sender, recipients, subject, words or
// mailing list, matched all or any, and what to do with it. Rules run on new inbox mail in the
// background (MailRulesBackgroundService), in their order; "Run on inbox now" runs them on what's there.
public static class MailRuleFields
{
    public const string From = "from";
    public const string To = "to";
    public const string Subject = "subject";
    public const string Words = "words";
    public const string List = "list";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
        [(From, "From"), (To, "To"), (Subject, "Subject"), (Words, "Subject or preview has"), (List, "Mailing list")];
}

public static class MailRuleOps
{
    public const string Contains = "contains";
    public const string Is = "is";
    public const string NotContains = "not-contains";

    public static readonly IReadOnlyList<(string Key, string Label)> All = [(Contains, "contains"), (Is, "is"), (NotContains, "doesn't contain")];
}

public static class MailRuleActionKinds
{
    public const string Folder = "folder";
    public const string Tab = "tab";
    public const string Read = "read";
    public const string Star = "star";
    public const string Archive = "archive";
}

public sealed record MailRuleCondition(string Field, string Op, string Value);

// Target: the folder key (MailFolders.FolderPrefix / LabelPrefix) or tab key (MailTabs), else empty.
public sealed record MailRuleAction(string Kind, string Target = "");

public sealed record MailRuleView(Guid Id, string Name, bool Enabled, bool MatchAll, IReadOnlyList<MailRuleCondition> Conditions,
    IReadOnlyList<MailRuleAction> Actions, int Order);

public static class MailRuleEngine
{
    public const int MaxRules = 100;
    public const int MaxConditions = 10;
    public const int MaxValueLength = 200;

    // Whether a rule's conditions hold for a message. Matching ignores case; "is" on From or To compares
    // whole addresses, so "is amazon.com" never matches; "contains" takes part of one ("@amazon.com").
    public static bool Matches(MailRuleView rule, MailInboxItem item)
    {
        if (rule.Conditions.Count == 0) return false;
        var results = rule.Conditions.Select(condition => Holds(condition, item));
        return rule.MatchAll ? results.All(held => held) : results.Any(held => held);
    }

    private static bool Holds(MailRuleCondition condition, MailInboxItem item)
    {
        var value = condition.Value.Trim();
        if (value.Length == 0) return false;
        var summary = item.Summary;
        IEnumerable<string> texts = condition.Field switch
        {
            MailRuleFields.From => [summary.From.Address, summary.From.Name],
            MailRuleFields.To => summary.To.SelectMany(to => new[] { to.Address, to.Name }),
            MailRuleFields.Subject => [summary.Subject],
            MailRuleFields.Words => [summary.Subject, summary.Snippet],
            MailRuleFields.List => [item.Headers.TryGetValue("List-Id", out var list) ? list : string.Empty],
            _ => []
        };
        var candidates = texts.Where(text => !string.IsNullOrEmpty(text)).ToList();
        return condition.Op switch
        {
            MailRuleOps.Is => candidates.Any(text => string.Equals(text.Trim(), value, StringComparison.OrdinalIgnoreCase)),
            MailRuleOps.NotContains => !candidates.Any(text => text.Contains(value, StringComparison.OrdinalIgnoreCase)),
            _ => candidates.Any(text => text.Contains(value, StringComparison.OrdinalIgnoreCase)),
        };
    }

    // The order a matched message's actions run in: flags first, then the tab, then whatever takes it
    // out of the inbox (a folder, or Archive), which is last because the message has a new id after.
    public static IReadOnlyList<MailRuleAction> InRunOrder(IEnumerable<MailRuleAction> actions) =>
        actions.OrderBy(action => action.Kind switch
        {
            MailRuleActionKinds.Read => 0,
            MailRuleActionKinds.Star => 1,
            MailRuleActionKinds.Tab => 2,
            _ => 3
        }).ToList();

    // A rule as it may be saved; throws MailException for anything that wouldn't run.
    public static MailRuleView Validate(MailRuleView rule)
    {
        var name = rule.Name.Trim();
        if (name.Length == 0) throw new MailException("Give the rule a name.");
        if (name.Length > 100) throw new MailException("Rule names can be up to 100 characters.");
        var conditions = rule.Conditions.Where(condition => !string.IsNullOrWhiteSpace(condition.Value)).ToList();
        if (conditions.Count == 0) throw new MailException("Add at least one condition.");
        if (conditions.Count > MaxConditions) throw new MailException($"A rule can have up to {MaxConditions} conditions.");
        foreach (var condition in conditions)
        {
            if (MailRuleFields.All.All(field => field.Key != condition.Field)) throw new MailException("Pick what to check.");
            if (MailRuleOps.All.All(op => op.Key != condition.Op)) throw new MailException("Pick how to compare.");
            if (condition.Value.Trim().Length > MaxValueLength) throw new MailException($"Condition values can be up to {MaxValueLength} characters.");
        }
        var actions = rule.Actions.DistinctBy(action => action.Kind).ToList();
        if (actions.Count == 0) throw new MailException("Choose at least one thing to do.");
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case MailRuleActionKinds.Folder when !MailFolders.IsKnown(action.Target) || !(action.Target.StartsWith(MailFolders.FolderPrefix, StringComparison.Ordinal) || action.Target.StartsWith(MailFolders.LabelPrefix, StringComparison.Ordinal)):
                    throw new MailException("Pick the folder to move it to.");
                case MailRuleActionKinds.Tab when MailTabs.Find(action.Target) is null:
                    throw new MailException("Pick the tab to move it to.");
                case MailRuleActionKinds.Folder or MailRuleActionKinds.Tab or MailRuleActionKinds.Read or MailRuleActionKinds.Star or MailRuleActionKinds.Archive:
                    break;
                default:
                    throw new MailException("That isn't something a rule can do.");
            }
        }
        if (actions.Any(action => action.Kind == MailRuleActionKinds.Folder) && actions.Any(action => action.Kind == MailRuleActionKinds.Archive))
            throw new MailException("A message can go to a folder or be archived, not both.");
        return rule with
        {
            Name = name,
            Conditions = conditions.Select(condition => condition with { Value = condition.Value.Trim() }).ToList(),
            Actions = actions
        };
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value);

    public static IReadOnlyList<T> FromJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
