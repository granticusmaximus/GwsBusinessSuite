using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public sealed record WikiDatabasePropertyOption(string Id, string Label, string Color);

public sealed record WikiDatabaseFilter(string PropertyId, string Operator, string Value);

public sealed record WikiDatabaseSort(string PropertyId, string Direction);

public sealed record WikiDatabaseViewConfig(
    IReadOnlyList<WikiDatabaseFilter> Filters,
    IReadOnlyList<WikiDatabaseSort> Sorts,
    string? GroupByPropertyId)
{
    public static WikiDatabaseViewConfig Empty { get; } = new([], [], null);
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
                : new WikiDatabaseViewConfig(parsed.Filters ?? [], parsed.Sorts ?? [], parsed.GroupByPropertyId);
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
}

public sealed class WikiDatabaseRowEditor
{
    public Guid? Id { get; set; }
    // Null means preserve the existing page body during a property-only edit.
    public string? BlocksJson { get; set; }
    // Keyed by property id (as string) - value shape matches WikiPropertyValues' per-type
    // getters/setters (string/decimal/bool/string[]/ISO-8601 date string).
    public Dictionary<string, JsonNode?> Values { get; set; } = new();
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

    public static IReadOnlyList<string> GetMultiSelect(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList()
            : [];

    public static void SetMultiSelect(JsonObject values, Guid propertyId, IReadOnlyList<string> optionIds) =>
        values[propertyId.ToString()] = new JsonArray(optionIds.Select(id => (JsonNode)id).ToArray());

    // A single-line rendering of a value, used for Board cards / table cells outside of
    // edit mode. CreatedTime reads the row's own CreatedAt rather than PropertyValuesJson.
    public static string GetDisplayText(WikiDatabaseProperty property, JsonObject values, DateTimeOffset rowCreatedAt) =>
        property.Type switch
        {
            WikiDatabasePropertyTypes.CreatedTime => rowCreatedAt.ToLocalTime().ToString("MMM d, yyyy"),
            WikiDatabasePropertyTypes.Checkbox => GetCheckbox(values, property.Id) ? "✓" : string.Empty,
            WikiDatabasePropertyTypes.Number => GetNumber(values, property.Id)?.ToString() ?? string.Empty,
            WikiDatabasePropertyTypes.Date => GetDate(values, property.Id)?.ToLocalTime().ToString("MMM d, yyyy") ?? string.Empty,
            WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Files or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Relation =>
                string.Join(", ", WikiDatabasePropertyConfig.GetOptions(property).Count > 0
                    ? ResolveOptionLabels(property, GetMultiSelect(values, property.Id))
                    : GetMultiSelect(values, property.Id)),
            WikiDatabasePropertyTypes.Select => GetText(values, property.Id) is { } optionId
                ? ResolveOptionLabels(property, [optionId]).FirstOrDefault() ?? string.Empty
                : string.Empty,
            _ => GetText(values, property.Id) ?? string.Empty
        };

    private static IReadOnlyList<string> ResolveOptionLabels(WikiDatabaseProperty property, IReadOnlyList<string> optionIds)
    {
        var options = WikiDatabasePropertyConfig.GetOptions(property).ToDictionary(o => o.Id);
        return optionIds.Select(id => options.TryGetValue(id, out var option) ? option.Label : id).ToList();
    }
}

public static class WikiDatabasePropertyConfig
{
    public static IReadOnlyList<WikiDatabasePropertyOption> GetOptions(WikiDatabaseProperty property)
    {
        if (string.IsNullOrWhiteSpace(property.ConfigJson))
        {
            return [];
        }

        try { return JsonSerializer.Deserialize<PropertyConfigDto>(property.ConfigJson, WikiPropertyValues.Options)?.Options ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IReadOnlyList<WikiDatabasePropertyOption> options) =>
        JsonSerializer.Serialize(new PropertyConfigDto(options), WikiPropertyValues.Options);

    private sealed record PropertyConfigDto(IReadOnlyList<WikiDatabasePropertyOption> Options);
}

// Pure, DB-free filter/sort/group logic over an already-loaded row list - same split as
// WikiBlockHtmlRenderer vs. WikiService: this is the unit-testable half, WikiDatabaseService
// owns loading rows from the database.
public static class WikiDatabaseViewLogic
{
    public static IReadOnlyList<WikiDatabaseRow> ApplyFilters(
        IReadOnlyList<WikiDatabaseRow> rows,
        IReadOnlyList<WikiDatabaseProperty> properties,
        IReadOnlyList<WikiDatabaseFilter> filters)
    {
        if (filters.Count == 0)
        {
            return rows;
        }

        var propertiesById = properties.ToDictionary(p => p.Id);
        return rows.Where(row => filters.All(filter => MatchesFilter(row, propertiesById, filter))).ToList();
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
            WikiDatabasePropertyTypes.Select => string.Equals(WikiPropertyValues.GetText(values, propertyId), filter.Value, StringComparison.Ordinal),
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
            _ => true
        };
    }

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

            object? KeySelector(WikiDatabaseRow row)
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                return property.Type switch
                {
                    WikiDatabasePropertyTypes.Number => WikiPropertyValues.GetNumber(values, propertyId),
                    WikiDatabasePropertyTypes.Date => WikiPropertyValues.GetDate(values, propertyId),
                    WikiDatabasePropertyTypes.Checkbox => WikiPropertyValues.GetCheckbox(values, propertyId),
                    WikiDatabasePropertyTypes.CreatedTime => row.CreatedAt,
                    _ => WikiPropertyValues.GetText(values, propertyId)
                };
            }

            var descending = sort.Direction == "descending";
            ordered = ordered is null
                ? (descending ? rows.OrderByDescending(KeySelector) : rows.OrderBy(KeySelector))
                : (descending ? ordered.ThenByDescending(KeySelector) : ordered.ThenBy(KeySelector));
        }

        return ordered?.ToList() ?? rows.OrderBy(row => row.SortOrder).ToList();
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
}

public sealed record WikiDatabaseBoardGroup(string OptionId, string Label, IReadOnlyList<WikiDatabaseRow> Rows);

public sealed record WikiDatabaseCalendarDay(DateOnly Date, bool IsCurrentMonth, IReadOnlyList<WikiDatabaseRow> Rows);

public sealed record WikiDatabaseCalendarMonth(
    DateOnly Month,
    IReadOnlyList<WikiDatabaseCalendarDay> Days,
    IReadOnlyList<WikiDatabaseRow> UndatedRows);

public sealed record WikiDatabaseTimelineGroup(DateOnly? Date, IReadOnlyList<WikiDatabaseRow> Rows);

public sealed record WikiDatabaseChartBucket(string Label, int Count);

public sealed record WikiInlineDatabaseProperty(
    Guid Id,
    string Name,
    string Type,
    bool IsReadOnly,
    IReadOnlyList<WikiDatabasePropertyOption> Options);

public sealed record WikiInlineDatabaseCell(Guid PropertyId, string Value);

public sealed record WikiInlineDatabaseRow(Guid Id, IReadOnlyList<WikiInlineDatabaseCell> Cells);

public sealed record WikiInlineDatabaseSnapshot(
    Guid Id,
    string Title,
    string Icon,
    IReadOnlyList<WikiInlineDatabaseProperty> Properties,
    IReadOnlyList<WikiInlineDatabaseRow> Rows);
