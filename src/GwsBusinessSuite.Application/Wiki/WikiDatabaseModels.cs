using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Group is only meaningful for a Status property (WikiDatabaseStatusGroups) - Select and
// MultiSelect options simply leave it null.
public sealed record WikiDatabasePropertyOption(string Id, string Label, string Color, string? Group = null);

public sealed record WikiDatabasePropertyConfiguration(
    IReadOnlyList<WikiDatabasePropertyOption> Options,
    string? FormulaExpression,
    Guid? RelatedDatabaseId,
    Guid? ReciprocalPropertyId,
    Guid? RelationPropertyId,
    Guid? RollupPropertyId,
    string? RollupAggregation,
    // Button property only: which Automation workflow a click runs, and the label shown on
    // the button (falls back to the property's own Name when blank).
    Guid? AutomationWorkflowId = null,
    string? ButtonLabel = null,
    // UniqueId property only: optional short prefix shown before the number (e.g. "TASK-42").
    string? UniqueIdPrefix = null,
    // AiField property only: a template referencing other columns via "[Property Name]"
    // (same bracket syntax WikiDatabaseComputation's formula engine already resolves by
    // name) and the Ollama model to call - see WikiDatabaseService.GenerateAiFieldValueAsync.
    string? AiPromptTemplate = null,
    string? AiModel = null)
{
    public static WikiDatabasePropertyConfiguration Empty { get; } = new([], null, null, null, null, null, null);
}

// Verification property value shape - stored as a compact JSON string in the row's scalar
// storage slot for that property (WikiPropertyValues.SetVerification/GetVerification).
public sealed record WikiVerificationState(string Status, string? VerifiedBy, DateTimeOffset? VerifiedAt)
{
    public const string Verified = "verified";
    public const string None = "none";

    public static WikiVerificationState NotVerified { get; } = new(None, null, null);
}

public static class WikiDatabaseRollupAggregations
{
    public const string Count = "count";
    public const string CountValues = "countValues";
    public const string Sum = "sum";
    public const string Average = "average";
    public const string Minimum = "minimum";
    public const string Maximum = "maximum";
    public const string ShowUnique = "showUnique";
    public const string CountEmpty = "countEmpty";
    public const string CountNotEmpty = "countNotEmpty";
    public const string PercentEmpty = "percentEmpty";
    public const string PercentNotEmpty = "percentNotEmpty";
    public const string Median = "median";
    public const string Range = "range";

    public static IReadOnlyList<string> All { get; } =
        [Count, CountValues, Sum, Average, Minimum, Maximum, ShowUnique,
         CountEmpty, CountNotEmpty, PercentEmpty, PercentNotEmpty, Median, Range];
}

public sealed record WikiDatabaseFilter(string PropertyId, string Operator, string Value);

public sealed record WikiDatabaseSort(string PropertyId, string Direction);

// A nested AND/OR filter tree, mirroring Notion's filter-group builder. Kept as a separate,
// optional field alongside the legacy flat WikiDatabaseFilter list (rather than replacing it)
// so already-saved views' ConfigJson keeps parsing unchanged - when FilterGroup is null or
// empty, WikiDatabaseViewLogic.ApplyFilters falls back to the flat implicit-AND Filters list.
public sealed record WikiDatabaseFilterGroup(
    string Combinator,
    IReadOnlyList<WikiDatabaseFilter> Conditions,
    IReadOnlyList<WikiDatabaseFilterGroup> Groups)
{
    public const string And = "and";
    public const string Or = "or";

    public static WikiDatabaseFilterGroup Empty { get; } = new(And, [], []);

    public bool IsEmpty => Conditions.Count == 0 && Groups.Count == 0;
}

public static class WikiDatabaseOpenPageModes
{
    public const string SidePeek = "sidePeek";
    public const string CenterPeek = "centerPeek";
    public const string FullPage = "fullPage";

    public static IReadOnlyList<string> All { get; } = [SidePeek, CenterPeek, FullPage];

    public static string Resolve(string? mode, string viewType)
    {
        if (All.Contains(mode, StringComparer.Ordinal))
        {
            return mode!;
        }

        return viewType is WikiDatabaseViewTypes.Gallery or WikiDatabaseViewTypes.Calendar
            ? CenterPeek
            : SidePeek;
    }
}

public sealed record WikiDatabaseViewConfig(
    IReadOnlyList<WikiDatabaseFilter> Filters,
    IReadOnlyList<WikiDatabaseSort> Sorts,
    string? GroupByPropertyId,
    string? OpenPageMode = null,
    IReadOnlyList<string>? PagePropertyOrder = null,
    IReadOnlyList<string>? HiddenPagePropertyIds = null,
    IReadOnlyDictionary<string, string>? Calculations = null,
    WikiDatabaseFilterGroup? FilterGroup = null,
    string? DependencyPropertyId = null,
    // Phase 5.1 - which of WikiDatabaseChartTypes a Chart view renders as; null/unrecognized
    // falls back to Bar (the original, only chart type before this field existed).
    string? ChartType = null)
{
    public static WikiDatabaseViewConfig Empty { get; } = new([], [], null, null, [], [], new Dictionary<string, string>(), null, null, null);
}

public static class WikiDatabaseChartTypes
{
    public const string Bar = "bar";
    public const string Line = "line";
    public const string Donut = "donut";
}

public static class WikiDatabasePagePresentation
{
    public static IReadOnlyList<WikiDatabaseProperty> OrderProperties(
        IReadOnlyList<WikiDatabaseProperty> properties,
        WikiDatabaseViewConfig config)
    {
        var explicitOrder = (config.PagePropertyOrder ?? [])
            .Select((propertyId, index) => (propertyId, index))
            .GroupBy(item => item.propertyId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);

        return properties
            .Where(property => property.Type != WikiDatabasePropertyTypes.Title)
            .OrderBy(property => explicitOrder.TryGetValue(property.Id.ToString(), out var index) ? index : int.MaxValue)
            .ThenBy(property => property.SortOrder)
            .ThenBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<WikiDatabaseProperty> VisibleProperties(
        IReadOnlyList<WikiDatabaseProperty> properties,
        WikiDatabaseViewConfig config)
    {
        var hidden = (config.HiddenPagePropertyIds ?? []).ToHashSet(StringComparer.Ordinal);
        return OrderProperties(properties, config)
            .Where(property => !hidden.Contains(property.Id.ToString()))
            .ToList();
    }
}

public sealed record WikiDatabaseTemplateProperty(
    Guid Id,
    string Name,
    string Type,
    int SortOrder,
    string ConfigJson);

public sealed record WikiDatabaseTemplateRow(
    Guid Id,
    int SortOrder,
    string PropertyValuesJson,
    string BlocksJson);

public sealed record WikiDatabaseTemplateView(
    Guid Id,
    string Name,
    string Type,
    int SortOrder,
    string ConfigJson);

public sealed record WikiDatabaseTemplateSnapshot(
    string Title,
    string? Icon,
    IReadOnlyList<WikiDatabaseTemplateProperty> Properties,
    IReadOnlyList<WikiDatabaseTemplateRow> Rows,
    IReadOnlyList<WikiDatabaseTemplateView> Views)
{
    public Guid? SourceDatabaseId { get; init; }
}

public static class WikiDatabaseViewConfigJson
{
    public static WikiDatabaseViewConfig Parse(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return WikiDatabaseViewConfig.Empty;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<WikiDatabaseViewConfig>(configJson, WikiPropertyValues.Options);
            return parsed is null
                ? WikiDatabaseViewConfig.Empty
                : new WikiDatabaseViewConfig(
                    parsed.Filters ?? [],
                    parsed.Sorts ?? [],
                    parsed.GroupByPropertyId,
                    parsed.OpenPageMode,
                    parsed.PagePropertyOrder ?? [],
                    parsed.HiddenPagePropertyIds ?? [],
                    parsed.Calculations ?? new Dictionary<string, string>(),
                    parsed.FilterGroup,
                    parsed.DependencyPropertyId,
                    parsed.ChartType);
        }
        catch (JsonException) { return WikiDatabaseViewConfig.Empty; }
    }

    public static string Serialize(WikiDatabaseViewConfig config) => JsonSerializer.Serialize(config, WikiPropertyValues.Options);
}

public sealed class WikiDatabasePropertyEditor
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = WikiDatabasePropertyTypes.Text;
    public IReadOnlyList<WikiDatabasePropertyOption> Options { get; set; } = [];
    public string? FormulaExpression { get; set; }
    public Guid? RelatedDatabaseId { get; set; }
    public Guid? ReciprocalPropertyId { get; set; }
    // Null preserves the current reciprocal setting; true creates/updates the paired
    // property; false removes both sides of the pairing. This command flag is not persisted.
    public bool? ReciprocalRelationEnabled { get; set; }
    public string? ReciprocalPropertyName { get; set; }
    public Guid? RelationPropertyId { get; set; }
    public Guid? RollupPropertyId { get; set; }
    public string? RollupAggregation { get; set; }
    public Guid? AutomationWorkflowId { get; set; }
    public string? ButtonLabel { get; set; }
    public string? UniqueIdPrefix { get; set; }
    public string? AiPromptTemplate { get; set; }
    public string? AiModel { get; set; }
}

public sealed class WikiDatabaseRowEditor
{
    public Guid? Id { get; set; }
    // Unlike the nullable page-content fields below, null is an explicit value here: it makes
    // the row a root item. Callers editing an existing child must therefore carry its current
    // ParentRowId forward unless they deliberately want to promote it.
    public Guid? ParentRowId { get; set; }
    // Null means preserve the existing page body during a property-only edit.
    public string? BlocksJson { get; set; }
    // Same null-preserves convention as BlocksJson above - a property-only save (e.g.
    // AddInlineRowAsync's blank editor) should not clobber an already-set icon/cover.
    public string? Icon { get; set; }
    public string? CoverImageUrl { get; set; }
    // Keyed by property id (as string) - value shape matches WikiPropertyValues' per-type
    // getters/setters (string/decimal/bool/string[]/ISO-8601 date string).
    public Dictionary<string, JsonNode?> Values { get; set; } = new();
    // Silent background autosave (SentinelDatabaseRowPage) sets this false so a debounced
    // save-per-keystroke-burst doesn't spam history the way an explicit "Save page" click
    // should. Defaults true so every other caller (property-only edits, tests, the explicit
    // save button) keeps today's "content save = new version" behavior unchanged.
    public bool CreateRevisionCheckpoint { get; set; } = true;
}

// Reads/writes a WikiDatabaseRow.PropertyValuesJson object, one typed accessor pair per
// WikiDatabasePropertyTypes value. Callers always know a value's property Type before
// reading it (they're iterating WikiDatabaseProperty rows), so these don't defensively
// guard against reading the wrong CLR type for what's actually stored - same "trust
// internal data" stance the rest of this codebase takes for self-authored JSON.
public static class WikiPropertyValues
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static JsonObject ParseObject(string propertyValuesJson)
    {
        if (string.IsNullOrWhiteSpace(propertyValuesJson))
        {
            return new JsonObject();
        }

        try { return JsonNode.Parse(propertyValuesJson)?.AsObject() ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static string Serialize(JsonObject values) => values.ToJsonString(Options);

    public static string? GetText(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) ? node?.GetValue<string>() : null;

    public static void SetText(JsonObject values, Guid propertyId, string? value) => values[propertyId.ToString()] = value;

    public static decimal? GetNumber(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is not null ? node.GetValue<decimal>() : null;

    public static void SetNumber(JsonObject values, Guid propertyId, decimal? value) => values[propertyId.ToString()] = value;

    public static bool GetCheckbox(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is not null && node.GetValue<bool>();

    public static void SetCheckbox(JsonObject values, Guid propertyId, bool value) => values[propertyId.ToString()] = value;

    public static DateTimeOffset? GetDate(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is not null
            && DateTimeOffset.TryParse(node.GetValue<string>(), out var date)
            ? date
            : null;

    public static void SetDate(JsonObject values, Guid propertyId, DateTimeOffset? value) =>
        values[propertyId.ToString()] = value?.ToString("O");

    public static WikiVerificationState GetVerification(JsonObject values, Guid propertyId)
    {
        if (!values.TryGetPropertyValue(propertyId.ToString(), out var node) || node is null)
        {
            return WikiVerificationState.NotVerified;
        }
        try
        {
            return JsonSerializer.Deserialize<WikiVerificationState>(node.GetValue<string>(), Options) ?? WikiVerificationState.NotVerified;
        }
        catch (JsonException) { return WikiVerificationState.NotVerified; }
    }

    public static void SetVerification(JsonObject values, Guid propertyId, WikiVerificationState state) =>
        values[propertyId.ToString()] = JsonSerializer.Serialize(state, Options);

    public static IReadOnlyList<string> GetMultiSelect(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList()
            : [];

    public static void SetMultiSelect(JsonObject values, Guid propertyId, IReadOnlyList<string> optionIds) =>
        values[propertyId.ToString()] = new JsonArray(optionIds.Select(id => (JsonNode)id).ToArray());

    // A single-line rendering of a value, used for Board cards / table cells outside of
    // edit mode. CreatedTime reads the row's own CreatedAt rather than PropertyValuesJson.
    public static string GetDisplayText(WikiDatabaseProperty property, JsonObject values, DateTimeOffset rowCreatedAt) =>
        GetDisplayText(property, values, rowCreatedAt, null, null, null);

    public static string GetDisplayText(
        WikiDatabaseProperty property,
        JsonObject values,
        DateTimeOffset rowCreatedAt,
        DateTimeOffset? rowUpdatedAt,
        string? rowCreatedBy,
        string? rowUpdatedBy) =>
        property.Type switch
        {
            WikiDatabasePropertyTypes.CreatedTime => rowCreatedAt.ToLocalTime().ToString("MMM d, yyyy"),
            WikiDatabasePropertyTypes.LastEditedTime => (rowUpdatedAt ?? rowCreatedAt).ToLocalTime().ToString("MMM d, yyyy"),
            WikiDatabasePropertyTypes.CreatedBy => rowCreatedBy ?? string.Empty,
            WikiDatabasePropertyTypes.LastEditedBy => rowUpdatedBy ?? rowCreatedBy ?? string.Empty,
            WikiDatabasePropertyTypes.Checkbox => GetCheckbox(values, property.Id) ? "✓" : string.Empty,
            WikiDatabasePropertyTypes.Number => GetNumber(values, property.Id)?.ToString() ?? string.Empty,
            WikiDatabasePropertyTypes.Date => GetDate(values, property.Id)?.ToLocalTime().ToString("MMM d, yyyy") ?? string.Empty,
            WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Files or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Relation =>
                string.Join(", ", WikiDatabasePropertyConfig.GetOptions(property).Count > 0
                    ? ResolveOptionLabels(property, GetMultiSelect(values, property.Id))
                    : GetMultiSelect(values, property.Id)),
            WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status => GetText(values, property.Id) is { } optionId
                ? ResolveOptionLabels(property, [optionId]).FirstOrDefault() ?? string.Empty
                : string.Empty,
            WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup => GetComputedDisplayText(values, property.Id),
            WikiDatabasePropertyTypes.Button => string.IsNullOrWhiteSpace(WikiDatabasePropertyConfig.Parse(property).ButtonLabel)
                ? property.Name
                : WikiDatabasePropertyConfig.Parse(property).ButtonLabel!,
            WikiDatabasePropertyTypes.UniqueId => GetNumber(values, property.Id) is { } uniqueIdNumber
                ? $"{WikiDatabasePropertyConfig.Parse(property).UniqueIdPrefix}{uniqueIdNumber:0}"
                : string.Empty,
            WikiDatabasePropertyTypes.Verification => GetVerification(values, property.Id) is { Status: WikiVerificationState.Verified } verified
                ? $"✓ Verified{(string.IsNullOrWhiteSpace(verified.VerifiedBy) ? "" : $" by {verified.VerifiedBy}")}"
                : "Not verified",
            _ => GetText(values, property.Id) ?? string.Empty
        };

    public static object? GetComputedValue(JsonObject values, Guid propertyId)
    {
        if (!values.TryGetPropertyValue(propertyId.ToString(), out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<decimal>(out var number)) return number;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.ToJsonString();
    }

    private static string GetComputedDisplayText(JsonObject values, Guid propertyId) =>
        GetComputedValue(values, propertyId) switch
        {
            null => string.Empty,
            bool boolean => boolean ? "True" : "False",
            decimal number => number.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
            object value => value.ToString() ?? string.Empty
        };

    private static IReadOnlyList<string> ResolveOptionLabels(WikiDatabaseProperty property, IReadOnlyList<string> optionIds)
    {
        var options = WikiDatabasePropertyConfig.GetOptions(property).ToDictionary(o => o.Id);
        return optionIds.Select(id => options.TryGetValue(id, out var option) ? option.Label : id).ToList();
    }
}

public static class WikiDatabasePropertyConfig
{
    public static WikiDatabasePropertyConfiguration Parse(WikiDatabaseProperty property) => Parse(property.ConfigJson);

    public static WikiDatabasePropertyConfiguration Parse(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return WikiDatabasePropertyConfiguration.Empty;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PropertyConfigDto>(configJson, WikiPropertyValues.Options);
            return parsed is null
                ? WikiDatabasePropertyConfiguration.Empty
                : new WikiDatabasePropertyConfiguration(
                    parsed.Options ?? [], parsed.FormulaExpression, parsed.RelatedDatabaseId,
                    parsed.ReciprocalPropertyId, parsed.RelationPropertyId,
                    parsed.RollupPropertyId, parsed.RollupAggregation,
                    parsed.AutomationWorkflowId, parsed.ButtonLabel, parsed.UniqueIdPrefix,
                    parsed.AiPromptTemplate, parsed.AiModel);
        }
        catch (JsonException) { return WikiDatabasePropertyConfiguration.Empty; }
    }

    public static IReadOnlyList<WikiDatabasePropertyOption> GetOptions(WikiDatabaseProperty property)
        => Parse(property).Options;

    public static string Serialize(IReadOnlyList<WikiDatabasePropertyOption> options) =>
        Serialize(new WikiDatabasePropertyConfiguration(options, null, null, null, null, null, null));

    public static string Serialize(WikiDatabasePropertyConfiguration configuration) =>
        JsonSerializer.Serialize(new PropertyConfigDto(
            configuration.Options,
            configuration.FormulaExpression,
            configuration.RelatedDatabaseId,
            configuration.ReciprocalPropertyId,
            configuration.RelationPropertyId,
            configuration.RollupPropertyId,
            configuration.RollupAggregation,
            configuration.AutomationWorkflowId,
            configuration.ButtonLabel,
            configuration.UniqueIdPrefix,
            configuration.AiPromptTemplate,
            configuration.AiModel), WikiPropertyValues.Options);

    private sealed record PropertyConfigDto(
        IReadOnlyList<WikiDatabasePropertyOption>? Options,
        string? FormulaExpression = null,
        Guid? RelatedDatabaseId = null,
        Guid? ReciprocalPropertyId = null,
        Guid? RelationPropertyId = null,
        Guid? RollupPropertyId = null,
        string? RollupAggregation = null,
        Guid? AutomationWorkflowId = null,
        string? ButtonLabel = null,
        string? UniqueIdPrefix = null,
        string? AiPromptTemplate = null,
        string? AiModel = null);
}

// Pure, DB-free filter/sort/group logic over an already-loaded row list - same split as
// WikiBlockHtmlRenderer vs. WikiService: this is the unit-testable half, WikiDatabaseService
// owns loading rows from the database.
public static class WikiDatabaseViewLogic
{
    public static IReadOnlyList<WikiDatabaseRow> ApplyFilters(
        IReadOnlyList<WikiDatabaseRow> rows,
        IReadOnlyList<WikiDatabaseProperty> properties,
        IReadOnlyList<WikiDatabaseFilter> filters,
        WikiDatabaseFilterGroup? filterGroup = null)
    {
        var propertiesById = properties.ToDictionary(p => p.Id);

        if (filterGroup is { IsEmpty: false } group)
        {
            return rows.Where(row => MatchesGroup(row, propertiesById, group)).ToList();
        }

        if (filters.Count == 0)
        {
            return rows;
        }

        return rows.Where(row => filters.All(filter => MatchesFilter(row, propertiesById, filter))).ToList();
    }

    private static bool MatchesGroup(WikiDatabaseRow row, IReadOnlyDictionary<Guid, WikiDatabaseProperty> propertiesById, WikiDatabaseFilterGroup group)
    {
        var results = group.Conditions.Select(condition => MatchesFilter(row, propertiesById, condition))
            .Concat(group.Groups.Select(nested => MatchesGroup(row, propertiesById, nested)));
        return group.Combinator == WikiDatabaseFilterGroup.Or ? results.Any(matched => matched) : results.All(matched => matched);
    }

    private static bool MatchesFilter(WikiDatabaseRow row, IReadOnlyDictionary<Guid, WikiDatabaseProperty> propertiesById, WikiDatabaseFilter filter)
    {
        if (!Guid.TryParse(filter.PropertyId, out var propertyId) || !propertiesById.TryGetValue(propertyId, out var property))
        {
            return true;
        }

        var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
        return property.Type switch
        {
            WikiDatabasePropertyTypes.Text or WikiDatabasePropertyTypes.Title or WikiDatabasePropertyTypes.Url => filter.Operator switch
            {
                "equals" => string.Equals(WikiPropertyValues.GetText(values, propertyId), filter.Value, StringComparison.OrdinalIgnoreCase),
                "contains" => (WikiPropertyValues.GetText(values, propertyId) ?? string.Empty).Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
                _ => true
            },
            WikiDatabasePropertyTypes.Number => WikiPropertyValues.GetNumber(values, propertyId) is { } number && decimal.TryParse(filter.Value, out var target)
                ? filter.Operator switch
                {
                    "equals" => number == target,
                    "greaterThan" => number > target,
                    "lessThan" => number < target,
                    _ => true
                }
                : false,
            WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status =>
                string.Equals(WikiPropertyValues.GetText(values, propertyId), filter.Value, StringComparison.Ordinal),
            WikiDatabasePropertyTypes.Checkbox => WikiPropertyValues.GetCheckbox(values, propertyId) == (filter.Operator == "isChecked"),
            WikiDatabasePropertyTypes.Date => WikiPropertyValues.GetDate(values, propertyId) is { } date && DateTimeOffset.TryParse(filter.Value, out var targetDate)
                ? filter.Operator switch
                {
                    "before" => date < targetDate,
                    "after" => date > targetDate,
                    "equals" => date.Date == targetDate.Date,
                    _ => true
                }
                : false,
            WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup =>
                MatchesComputedFilter(WikiPropertyValues.GetComputedValue(values, propertyId), filter),
            _ => true
        };
    }

    private static bool MatchesComputedFilter(object? value, WikiDatabaseFilter filter) => value switch
    {
        decimal number when decimal.TryParse(filter.Value, out var target) => filter.Operator switch
        {
            "equals" => number == target,
            "greaterThan" => number > target,
            "lessThan" => number < target,
            _ => true
        },
        bool boolean => filter.Operator switch
        {
            "isChecked" => boolean,
            "isNotChecked" => !boolean,
            "equals" => bool.TryParse(filter.Value, out var target) && boolean == target,
            _ => true
        },
        string text => filter.Operator switch
        {
            "equals" => string.Equals(text, filter.Value, StringComparison.OrdinalIgnoreCase),
            "contains" => text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => true
        },
        _ => false
    };

    public static IReadOnlyList<WikiDatabaseRow> ApplySort(
        IReadOnlyList<WikiDatabaseRow> rows,
        IReadOnlyList<WikiDatabaseProperty> properties,
        IReadOnlyList<WikiDatabaseSort> sorts)
    {
        if (sorts.Count == 0)
        {
            return rows.OrderBy(row => row.SortOrder).ToList();
        }

        var propertiesById = properties.ToDictionary(p => p.Id);
        IOrderedEnumerable<WikiDatabaseRow>? ordered = null;
        foreach (var sort in sorts)
        {
            if (!Guid.TryParse(sort.PropertyId, out var propertyId) || !propertiesById.TryGetValue(propertyId, out var property))
            {
                continue;
            }

            WikiDatabaseSortValue KeySelector(WikiDatabaseRow row)
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                var value = property.Type switch
                {
                    WikiDatabasePropertyTypes.Number => (object?)WikiPropertyValues.GetNumber(values, propertyId),
                    WikiDatabasePropertyTypes.Date => WikiPropertyValues.GetDate(values, propertyId),
                    WikiDatabasePropertyTypes.Checkbox => WikiPropertyValues.GetCheckbox(values, propertyId),
                    WikiDatabasePropertyTypes.CreatedTime => row.CreatedAt,
                    WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup => WikiPropertyValues.GetComputedValue(values, propertyId),
                    _ => WikiPropertyValues.GetText(values, propertyId)
                };
                return WikiDatabaseSortValue.From(value);
            }

            var descending = sort.Direction == "descending";
            ordered = ordered is null
                ? (descending ? rows.OrderByDescending(KeySelector) : rows.OrderBy(KeySelector))
                : (descending ? ordered.ThenByDescending(KeySelector) : ordered.ThenBy(KeySelector));
        }

        return ordered?.ToList() ?? rows.OrderBy(row => row.SortOrder).ToList();
    }

    private readonly record struct WikiDatabaseSortValue(int TypeOrder, decimal Number, long Ticks, string Text)
        : IComparable<WikiDatabaseSortValue>
    {
        public static WikiDatabaseSortValue From(object? value) => value switch
        {
            decimal number => new(1, number, 0, string.Empty),
            bool boolean => new(2, boolean ? 1 : 0, 0, string.Empty),
            DateTimeOffset date => new(3, 0, date.UtcTicks, string.Empty),
            null => new(0, 0, 0, string.Empty),
            _ => new(4, 0, 0, value.ToString() ?? string.Empty)
        };

        public int CompareTo(WikiDatabaseSortValue other)
        {
            var typeComparison = TypeOrder.CompareTo(other.TypeOrder);
            if (typeComparison != 0) return typeComparison;
            return TypeOrder switch
            {
                1 or 2 => Number.CompareTo(other.Number),
                3 => Ticks.CompareTo(other.Ticks),
                _ => string.Compare(Text, other.Text, StringComparison.OrdinalIgnoreCase)
            };
        }
    }

    public static IReadOnlyList<WikiDatabaseBoardGroup> GroupForBoard(
        IReadOnlyList<WikiDatabaseRow> rows,
        WikiDatabaseProperty groupByProperty)
    {
        var options = WikiDatabasePropertyConfig.GetOptions(groupByProperty);
        var byOption = rows
            .Select(row => (Row: row, OptionId: WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), groupByProperty.Id) ?? string.Empty))
            .ToLookup(entry => entry.OptionId);

        var groups = options
            .Select(option => new WikiDatabaseBoardGroup(
                option.Id,
                option.Label,
                byOption[option.Id].Select(entry => entry.Row).OrderBy(row => row.SortOrder).ToList()))
            .ToList();

        groups.Add(new WikiDatabaseBoardGroup(
            string.Empty,
            "No status",
            byOption[string.Empty].Select(entry => entry.Row).OrderBy(row => row.SortOrder).ToList()));

        return groups;
    }

    public static WikiDatabaseCalendarMonth BuildCalendarMonth(
        IReadOnlyList<WikiDatabaseRow> rows,
        WikiDatabaseProperty dateProperty,
        DateOnly month)
    {
        if (dateProperty.Type != WikiDatabasePropertyTypes.Date)
        {
            throw new ArgumentException("Calendar views require a Date property.", nameof(dateProperty));
        }

        var firstOfMonth = new DateOnly(month.Year, month.Month, 1);
        var leadingDays = (int)firstOfMonth.DayOfWeek;
        var gridStart = firstOfMonth.AddDays(-leadingDays);
        var datedRows = rows
            .Select(row => (
                Row: row,
                Date: WikiPropertyValues.GetDate(WikiPropertyValues.ParseObject(row.PropertyValuesJson), dateProperty.Id)))
            .ToList();
        var rowsByDate = datedRows
            .Where(item => item.Date.HasValue)
            .ToLookup(item => DateOnly.FromDateTime(item.Date!.Value.ToLocalTime().DateTime), item => item.Row);
        var days = Enumerable.Range(0, 42)
            .Select(offset =>
            {
                var date = gridStart.AddDays(offset);
                return new WikiDatabaseCalendarDay(
                    date,
                    date.Month == firstOfMonth.Month,
                    rowsByDate[date].ToList());
            })
            .ToList();
        var undated = datedRows
            .Where(item => !item.Date.HasValue)
            .Select(item => item.Row)
            .ToList();

        return new WikiDatabaseCalendarMonth(firstOfMonth, days, undated);
    }

    public static IReadOnlyList<WikiDatabaseTimelineGroup> BuildTimeline(
        IReadOnlyList<WikiDatabaseRow> rows,
        WikiDatabaseProperty dateProperty)
    {
        if (dateProperty.Type != WikiDatabasePropertyTypes.Date)
        {
            throw new ArgumentException("Timeline views require a Date property.", nameof(dateProperty));
        }

        return rows
            .Select(row => (Row: row, Date: WikiPropertyValues.GetDate(
                WikiPropertyValues.ParseObject(row.PropertyValuesJson), dateProperty.Id)))
            .GroupBy(item => item.Date is { } date
                ? new DateOnly?(DateOnly.FromDateTime(date.ToLocalTime().DateTime))
                : null)
            .OrderBy(group => group.Key is null)
            .ThenBy(group => group.Key)
            .Select(group => new WikiDatabaseTimelineGroup(
                group.Key,
                group.Select(item => item.Row).ToList()))
            .ToList();
    }

    public static WikiDatabaseTimelineSchedule BuildTimelineSchedule(
        IReadOnlyList<WikiDatabaseRow> rows,
        WikiDatabaseProperty dateProperty,
        WikiDatabaseProperty? dependencyProperty = null)
    {
        if (dateProperty.Type != WikiDatabasePropertyTypes.Date)
        {
            throw new ArgumentException("Timeline views require a Date property.", nameof(dateProperty));
        }
        if (dependencyProperty is not null && dependencyProperty.Type != WikiDatabasePropertyTypes.Relation)
        {
            throw new ArgumentException("Timeline dependencies require a Relation property.", nameof(dependencyProperty));
        }

        var items = rows
            .Select(row =>
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                var date = WikiPropertyValues.GetDate(values, dateProperty.Id) is { } storedDate
                    ? new DateOnly?(DateOnly.FromDateTime(storedDate.ToLocalTime().DateTime))
                    : null;
                IReadOnlyList<Guid> dependencies = dependencyProperty is null
                    ? Array.Empty<Guid>()
                    : WikiPropertyValues.GetMultiSelect(values, dependencyProperty.Id)
                        .Select(value => Guid.TryParse(value, out var rowId) ? new Guid?(rowId) : null)
                        .Where(rowId => rowId.HasValue)
                        .Select(rowId => rowId!.Value)
                        .Distinct()
                        .ToList();
                return new WikiDatabaseTimelineItem(row, date, dependencies);
            })
            .OrderBy(item => item.Date is null)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Row.SortOrder)
            .ToList();
        var datedItems = items.Where(item => item.Date.HasValue).ToList();

        return new WikiDatabaseTimelineSchedule(
            datedItems.FirstOrDefault()?.Date,
            datedItems.LastOrDefault()?.Date,
            datedItems,
            items.Where(item => item.Date is null).ToList());
    }

    public static void EnsureAcyclicTimelineDependencies(
        Guid changedRowId,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> dependenciesByRow)
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();

        void Visit(Guid rowId)
        {
            if (!visiting.Add(rowId))
            {
                throw new InvalidOperationException("Timeline dependencies cannot contain a cycle.");
            }
            if (visited.Contains(rowId))
            {
                visiting.Remove(rowId);
                return;
            }

            if (dependenciesByRow.TryGetValue(rowId, out var dependencies))
            {
                foreach (var dependencyRowId in dependencies)
                {
                    Visit(dependencyRowId);
                }
            }

            visiting.Remove(rowId);
            visited.Add(rowId);
        }

        Visit(changedRowId);
    }

    public static IReadOnlyList<WikiDatabaseChartBucket> BuildChart(
        IReadOnlyList<WikiDatabaseRow> rows,
        WikiDatabaseProperty property)
    {
        var configuredOptions = WikiDatabasePropertyConfig.GetOptions(property);
        var labels = configuredOptions.ToDictionary(option => option.Id, option => option.Label);
        var buckets = rows
            .SelectMany(row => property.Type == WikiDatabasePropertyTypes.MultiSelect
                ? WikiPropertyValues.GetMultiSelect(WikiPropertyValues.ParseObject(row.PropertyValuesJson), property.Id)
                : [WikiPropertyValues.GetDisplayText(property, WikiPropertyValues.ParseObject(row.PropertyValuesJson), row.CreatedAt)])
            .Select(value => labels.GetValueOrDefault(value, value))
            .Select(value => string.IsNullOrWhiteSpace(value) ? "Empty" : value)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WikiDatabaseChartBucket(group.Key, group.Count()))
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return buckets;
    }

    public static readonly IReadOnlyList<string> ChartPalette =
        ["#f59e0b", "#38bdf8", "#34d399", "#a78bfa", "#fb7185", "#facc15", "#2dd4bf", "#f472b6"];

    // A flat 0..100 viewBox regardless of bucket count/values - the consuming <svg>'s own
    // width/height does the scaling, so this never needs to know the rendered pixel size.
    public static string LinePoints(IReadOnlyList<WikiDatabaseChartBucket> buckets, int maximum)
    {
        if (buckets.Count == 0)
        {
            return string.Empty;
        }

        var stepX = buckets.Count == 1 ? 0 : 100d / (buckets.Count - 1);
        return string.Join(' ', buckets.Select((bucket, index) =>
        {
            var x = buckets.Count == 1 ? 50 : index * stepX;
            var y = 100 - (bucket.Count * 100d / maximum);
            return FormattableString.Invariant($"{x:0.##},{y:0.##}");
        }));
    }

    // Classic stroke-dasharray/stroke-dashoffset donut technique: each bucket becomes one arc of
    // a circle whose circumference is 2*pi*40 (r=40, matching a <circle r="40"> in the consuming
    // markup), with dashoffset accumulating so each arc starts where the previous one ended.
    public static IReadOnlyList<WikiDatabaseDonutSegment> DonutSegments(IReadOnlyList<WikiDatabaseChartBucket> buckets)
    {
        const double circumference = 251.327;
        var total = Math.Max(1, buckets.Sum(bucket => bucket.Count));
        var offset = 0d;
        var segments = new List<WikiDatabaseDonutSegment>();
        for (var index = 0; index < buckets.Count; index++)
        {
            var length = buckets[index].Count / (double)total * circumference;
            segments.Add(new WikiDatabaseDonutSegment(ChartPalette[index % ChartPalette.Count], length, -offset));
            offset += length;
        }

        return segments;
    }
}

public readonly record struct WikiDatabaseDonutSegment(string Color, double DashArrayValue, double DashOffset);

public sealed record WikiDatabaseBoardGroup(string OptionId, string Label, IReadOnlyList<WikiDatabaseRow> Rows);

public sealed record WikiDatabaseCalendarDay(DateOnly Date, bool IsCurrentMonth, IReadOnlyList<WikiDatabaseRow> Rows);

public sealed record WikiDatabaseCalendarMonth(
    DateOnly Month,
    IReadOnlyList<WikiDatabaseCalendarDay> Days,
    IReadOnlyList<WikiDatabaseRow> UndatedRows);

public sealed record WikiDatabaseTimelineGroup(DateOnly? Date, IReadOnlyList<WikiDatabaseRow> Rows);

public sealed record WikiDatabaseTimelineItem(
    WikiDatabaseRow Row,
    DateOnly? Date,
    IReadOnlyList<Guid> DependsOnRowIds);

public sealed record WikiDatabaseTimelineSchedule(
    DateOnly? StartDate,
    DateOnly? EndDate,
    IReadOnlyList<WikiDatabaseTimelineItem> DatedItems,
    IReadOnlyList<WikiDatabaseTimelineItem> UndatedItems);

public sealed record WikiDatabaseChartBucket(string Label, int Count);

public sealed record WikiInlineDatabaseProperty(
    Guid Id,
    string Name,
    string Type,
    bool IsReadOnly,
    IReadOnlyList<WikiDatabasePropertyOption> Options);

public sealed record WikiInlineDatabaseCell(Guid PropertyId, string Value);

public sealed record WikiInlineDatabaseRow(Guid Id, IReadOnlyList<WikiInlineDatabaseCell> Cells);

public sealed record WikiInlineDatabaseView(
    Guid Id,
    string Name,
    string Type,
    string? GroupByPropertyId);

public sealed record WikiInlineDatabaseSnapshot(
    Guid Id,
    string Title,
    string Icon,
    IReadOnlyList<WikiInlineDatabaseProperty> Properties,
    IReadOnlyList<WikiInlineDatabaseRow> Rows)
{
    public IReadOnlyList<WikiInlineDatabaseView> Views { get; init; } = [];
    public bool CanEdit { get; init; } = true;
}
