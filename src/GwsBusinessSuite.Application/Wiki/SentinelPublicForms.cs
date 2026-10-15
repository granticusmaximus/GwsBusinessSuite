using System.Globalization;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Public forms from a database (Sentinel item 32). A Form view's settings live in its
// WikiDatabaseViewConfig.FormSettings; "Share publicly" creates a SentinelPublicShare with
// FormViewId set, opened at /sentinel/form/{token}. Every answer is checked here on the server
// (the page's show/hide script is a convenience only): a hidden question's answer is dropped,
// required questions must be answered, and choices must be real options.
public sealed record WikiDatabaseFormSettings(
    string? Title = null,
    string? Description = null,
    string? ThankYouMessage = null,
    // Questions the form doesn't ask (null = every eligible property is asked).
    IReadOnlyList<string>? HiddenPropertyIds = null,
    // Required on this form, on top of properties that are Required everywhere.
    IReadOnlyList<string>? RequiredPropertyIds = null,
    IReadOnlyList<WikiDatabaseFormCondition>? Conditions = null);

// Ask PropertyId only when WhenPropertyId's answer matches (see WikiDatabaseFormOperators).
public sealed record WikiDatabaseFormCondition(string PropertyId, string WhenPropertyId, string Operator, string? Value = null);

public static class WikiDatabaseFormOperators
{
    public const string Is = "equals";
    public const string IsNot = "notEquals";
    public const string IsChecked = "isChecked";
    public const string IsNotChecked = "isNotChecked";
    public const string IsNotEmpty = "isNotEmpty";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        (Is, "is"), (IsNot, "is not"), (IsChecked, "is ticked"), (IsNotChecked, "is not ticked"), (IsNotEmpty, "is answered")
    ];

    public static bool NeedsValue(string op) => op is Is or IsNot;
}

public sealed record SentinelPublicFormResult(bool Saved, IReadOnlyList<string> Errors)
{
    public static SentinelPublicFormResult Failed(params string[] errors) => new(false, errors);
}

public interface ISentinelPublicFormService
{
    // The form behind a public form link, or null when the link is unknown, expired, revoked, or
    // its view/database is gone.
    Task<SentinelPublicFormView?> GetAsync(string token, CancellationToken cancellationToken = default);

    // answers: form field name (property id) -> submitted values.
    Task<SentinelPublicFormResult> SubmitAsync(string token, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, CancellationToken cancellationToken = default);
}

public sealed record SentinelPublicFormView(Guid ShareId, WikiDatabase Database, WikiDatabaseView View, WikiDatabaseFormSettings Settings)
{
    public string Title => string.IsNullOrWhiteSpace(Settings.Title) ? Database.Title : Settings.Title.Trim();
    public IReadOnlyList<WikiDatabaseProperty> Questions => SentinelPublicForm.Questions(Database, Settings);
}

public static class SentinelPublicForm
{
    public const string SubmittedBy = "public-form";
    public const int MaxAnswerLength = 4000;

    // Types a stranger can answer. Person, Relation and Files would show workspace users, other
    // databases' rows or need uploads; computed types can't be answered at all.
    public static readonly IReadOnlySet<string> AnswerableTypes = new HashSet<string>
    {
        WikiDatabasePropertyTypes.Title, WikiDatabasePropertyTypes.Text, WikiDatabasePropertyTypes.Number,
        WikiDatabasePropertyTypes.Select, WikiDatabasePropertyTypes.Status, WikiDatabasePropertyTypes.MultiSelect,
        WikiDatabasePropertyTypes.Date, WikiDatabasePropertyTypes.Checkbox, WikiDatabasePropertyTypes.Url,
        WikiDatabasePropertyTypes.Email, WikiDatabasePropertyTypes.Phone, WikiDatabasePropertyTypes.Place
    };

    public static IReadOnlyList<WikiDatabaseProperty> Questions(WikiDatabase database, WikiDatabaseFormSettings settings)
    {
        var hidden = (settings.HiddenPropertyIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return database.Properties
            .Where(property => AnswerableTypes.Contains(property.Type) && !hidden.Contains(property.Id.ToString()))
            .OrderBy(property => property.Type == WikiDatabasePropertyTypes.Title ? 0 : 1)
            .ThenBy(property => property.SortOrder)
            .ToList();
    }

    public static bool IsRequired(WikiDatabaseProperty property, WikiDatabaseFormSettings settings) =>
        WikiDatabasePropertyConfig.Parse(property).IsRequired
        || (settings.RequiredPropertyIds ?? []).Contains(property.Id.ToString(), StringComparer.OrdinalIgnoreCase);

    public static WikiDatabaseFormCondition? ConditionFor(WikiDatabaseProperty property, WikiDatabaseFormSettings settings) =>
        (settings.Conditions ?? []).FirstOrDefault(condition =>
            string.Equals(condition.PropertyId, property.Id.ToString(), StringComparison.OrdinalIgnoreCase));

    // Whether a question is asked, given the answers so far. A question whose condition depends
    // on a question that isn't asked (or on itself, through a loop) isn't asked either.
    public static bool IsShown(
        WikiDatabaseProperty property,
        IReadOnlyList<WikiDatabaseProperty> questions,
        WikiDatabaseFormSettings settings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
        IsShown(property, questions, settings, answers, []);

    private static bool IsShown(
        WikiDatabaseProperty property,
        IReadOnlyList<WikiDatabaseProperty> questions,
        WikiDatabaseFormSettings settings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        HashSet<Guid> visiting)
    {
        if (ConditionFor(property, settings) is not { } condition) return true;
        if (!visiting.Add(property.Id)) return false;
        var trigger = questions.FirstOrDefault(question => string.Equals(question.Id.ToString(), condition.WhenPropertyId, StringComparison.OrdinalIgnoreCase));
        if (trigger is null || !IsShown(trigger, questions, settings, answers, visiting)) return false;
        var given = Answer(answers, trigger.Id);
        var value = condition.Value?.Trim() ?? string.Empty;
        return condition.Operator switch
        {
            WikiDatabaseFormOperators.Is => given.Contains(value, StringComparer.OrdinalIgnoreCase),
            WikiDatabaseFormOperators.IsNot => !given.Contains(value, StringComparer.OrdinalIgnoreCase),
            WikiDatabaseFormOperators.IsChecked => given.Contains("true", StringComparer.OrdinalIgnoreCase),
            WikiDatabaseFormOperators.IsNotChecked => !given.Contains("true", StringComparer.OrdinalIgnoreCase),
            WikiDatabaseFormOperators.IsNotEmpty => given.Count > 0,
            _ => true
        };
    }

    // The new row's values from a submission, or the reasons it can't be saved.
    public static (JsonObject Values, IReadOnlyList<string> Errors) BuildRow(
        WikiDatabase database,
        WikiDatabaseFormSettings settings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var questions = Questions(database, settings);
        var values = new JsonObject();
        var errors = new List<string>();
        foreach (var question in questions)
        {
            if (!IsShown(question, questions, settings, answers)) continue;
            var given = Answer(answers, question.Id);
            if (given.Count == 0)
            {
                if (IsRequired(question, settings) && question.Type != WikiDatabasePropertyTypes.Checkbox)
                    errors.Add($"\"{question.Name}\" is required.");
                continue;
            }
            if (given.Any(answer => answer.Length > MaxAnswerLength))
            {
                errors.Add($"\"{question.Name}\" is too long.");
                continue;
            }
            switch (question.Type)
            {
                case WikiDatabasePropertyTypes.Number:
                    if (decimal.TryParse(given[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                        WikiPropertyValues.SetNumber(values, question.Id, number);
                    else errors.Add($"\"{question.Name}\" must be a number.");
                    break;
                case WikiDatabasePropertyTypes.Date:
                    if (DateOnly.TryParseExact(given[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        WikiPropertyValues.SetDate(values, question.Id, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
                    else errors.Add($"\"{question.Name}\" must be a date.");
                    break;
                case WikiDatabasePropertyTypes.Checkbox:
                    WikiPropertyValues.SetCheckbox(values, question.Id, given.Contains("true", StringComparer.OrdinalIgnoreCase));
                    break;
                case WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status or WikiDatabasePropertyTypes.MultiSelect:
                    var options = WikiDatabasePropertyConfig.GetOptions(question).Select(option => option.Id).ToHashSet(StringComparer.Ordinal);
                    if (given.Any(answer => !options.Contains(answer)))
                    {
                        errors.Add($"\"{question.Name}\" has a choice that isn't on the list.");
                        break;
                    }
                    if (question.Type == WikiDatabasePropertyTypes.MultiSelect) WikiPropertyValues.SetMultiSelect(values, question.Id, given.Distinct().ToList());
                    else WikiPropertyValues.SetText(values, question.Id, given[0]);
                    break;
                default:
                    WikiPropertyValues.SetText(values, question.Id, given[0].Trim());
                    break;
            }
        }
        return (values, errors);
    }

    // Non-blank submitted values for a question; a checkbox posts "true" only when ticked.
    private static IReadOnlyList<string> Answer(IReadOnlyDictionary<string, IReadOnlyList<string>> answers, Guid propertyId) =>
        answers.TryGetValue(propertyId.ToString(), out var given)
            ? given.Where(answer => !string.IsNullOrWhiteSpace(answer)).Select(answer => answer.Trim()).ToList()
            : [];
}
