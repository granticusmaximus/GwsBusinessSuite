using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Typed pages (Sentinel item 11, "supertags"). A type ("Person", "Meeting", "Client") has fields;
// giving a page that type adds those fields to the page, and the type lists every page that has
// it - filterable and sortable by field, like a database view, across the whole workspace.
// A page can have several types. Values are stored normalized (see NormalizeValue).
public static partial class SentinelPageTypes
{
    public const int MaxFields = 30;
    public const int MaxOptions = 50;
    public const int MaxTextValue = 2000;

    public static class Kinds
    {
        public const string Text = "text";
        public const string Number = "number";
        public const string Date = "date";
        public const string Select = "select";
        public const string Checkbox = "checkbox";
        public const string Url = "url";
        public const string Person = "person";
        public static readonly IReadOnlyList<string> All = [Text, Number, Date, Select, Checkbox, Url, Person];

        public static string Label(string kind) => kind switch
        {
            Number => "Number", Date => "Date", Select => "Select", Checkbox => "Checkbox", Url => "Link", Person => "Person", _ => "Text"
        };
    }

    public static class Operators
    {
        public const string Is = "is";
        public const string IsNot = "is_not";
        public const string Contains = "contains";
        public const string Empty = "empty";
        public const string NotEmpty = "not_empty";
        public const string Greater = "gt";
        public const string Less = "lt";

        public static IReadOnlyList<(string Key, string Label)> For(string kind) => kind switch
        {
            Kinds.Number or Kinds.Date => [(Is, "is"), (Greater, kind == Kinds.Date ? "after" : ">"), (Less, kind == Kinds.Date ? "before" : "<"), (Empty, "is empty"), (NotEmpty, "is not empty")],
            Kinds.Checkbox => [(Is, "is")],
            Kinds.Select or Kinds.Person => [(Is, "is"), (IsNot, "is not"), (Empty, "is empty"), (NotEmpty, "is not empty")],
            _ => [(Contains, "contains"), (Is, "is"), (Empty, "is empty"), (NotEmpty, "is not empty")]
        };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    public static IReadOnlyList<SentinelTypeField> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<SentinelTypeField>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeFields(IReadOnlyList<SentinelTypeField> fields) => JsonSerializer.Serialize(fields, Json);

    public static IReadOnlyDictionary<string, string> ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? []; }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    public static string SerializeValues(IReadOnlyDictionary<string, string> values) => JsonSerializer.Serialize(values, Json);

    // Throws ArgumentException with a message for the person editing the type.
    public static IReadOnlyList<SentinelTypeField> NormalizeFields(IEnumerable<SentinelTypeField> fields)
    {
        var result = new List<SentinelTypeField>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var name = (field.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > 40) throw new ArgumentException("Each field needs a name of up to 40 characters.");
            if (!names.Add(name)) throw new ArgumentException($"There are two fields called \"{name}\".");
            if (!Kinds.All.Contains(field.Kind)) throw new ArgumentException($"\"{field.Kind}\" isn't a field kind.");
            var id = string.IsNullOrWhiteSpace(field.Id) ? Guid.NewGuid().ToString("N")[..12] : field.Id.Trim();
            if (!FieldId().IsMatch(id) || !ids.Add(id)) throw new ArgumentException("A field has an invalid id.");
            var options = field.Kind == Kinds.Select
                ? (field.Options ?? []).Select(option => option.Trim()).Where(option => option.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (options.Count > MaxOptions || options.Any(option => option.Length > 60)) throw new ArgumentException($"A select field can have up to {MaxOptions} options of up to 60 characters.");
            result.Add(new SentinelTypeField(id, name, field.Kind, options));
        }
        if (result.Count > MaxFields) throw new ArgumentException($"A type can have up to {MaxFields} fields.");
        return result;
    }

    // The stored form of a value, or null to clear it. Throws ArgumentException for a value the
    // field can't hold.
    public static string? NormalizeValue(SentinelTypeField field, string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        switch (field.Kind)
        {
            case Kinds.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new ArgumentException($"{field.Name} needs a number.");
                return number.ToString("R", CultureInfo.InvariantCulture);
            case Kinds.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new ArgumentException($"{field.Name} needs a date.");
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case Kinds.Checkbox:
                return value is "true" or "True" or "1" or "on" ? "true" : null;
            case Kinds.Select:
                return field.Options.FirstOrDefault(option => option.Equals(value, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"\"{value}\" isn't one of {field.Name}'s options.");
            case Kinds.Url:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeMailto))
                    throw new ArgumentException($"{field.Name} needs a web address (https://...).");
                return uri.AbsoluteUri;
            case Kinds.Person:
                return value.Length > 100 ? value[..100] : value;
            default:
                return value.Length > MaxTextValue ? value[..MaxTextValue] : value;
        }
    }

    public static bool Matches(SentinelTypeField field, string? value, SentinelTypeFilter filter)
    {
        var has = !string.IsNullOrEmpty(value);
        var target = (filter.Value ?? string.Empty).Trim();
        switch (filter.Operator)
        {
            case Operators.Empty: return !has;
            case Operators.NotEmpty: return has;
        }
        if (field.Kind == Kinds.Checkbox)
        {
            var wanted = target is "true" or "1" or "on";
            return (value == "true") == wanted;
        }
        if (target.Length == 0) return true;
        switch (field.Kind)
        {
            case Kinds.Number:
                if (!has || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    || !double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
                return filter.Operator switch { Operators.Greater => a > b, Operators.Less => a < b, _ => a.Equals(b) };
            case Kinds.Date:
                if (!has) return false;
                var compare = string.CompareOrdinal(value, target);
                return filter.Operator switch { Operators.Greater => compare > 0, Operators.Less => compare < 0, _ => compare == 0 };
        }
        return filter.Operator switch
        {
            Operators.Is => has && value!.Equals(target, StringComparison.OrdinalIgnoreCase),
            Operators.IsNot => !has || !value!.Equals(target, StringComparison.OrdinalIgnoreCase),
            _ => has && value!.Contains(target, StringComparison.OrdinalIgnoreCase)
        };
    }

    // Numbers compare as numbers; everything else as text (dates are yyyy-MM-dd). Empty last.
    public static int Compare(SentinelTypeField field, string? left, string? right)
    {
        if (string.IsNullOrEmpty(left)) return string.IsNullOrEmpty(right) ? 0 : 1;
        if (string.IsNullOrEmpty(right)) return -1;
        if (field.Kind == Kinds.Number
            && double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return a.CompareTo(b);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    // For display: dates as dd-MM-yy, checkboxes as a tick.
    public static string Display(SentinelTypeField field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return field.Kind switch
        {
            Kinds.Date when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date.ToString("dd-MM-yy", CultureInfo.InvariantCulture),
            Kinds.Checkbox => value == "true" ? "✓" : string.Empty,
            _ => value
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex FieldId();
}

public sealed record SentinelTypeField(string Id, string Name, string Kind, IReadOnlyList<string> Options);

public sealed record SentinelTypeFilter(string FieldId, string Operator, string? Value);

public sealed record SentinelPageTypeView(
    Guid Id, string Name, string Icon, string Description, IReadOnlyList<SentinelTypeField> Fields, int PageCount, bool CanEdit);

public sealed record SentinelPageTypeEditor(Guid? Id, string Name, string Icon, string Description, IReadOnlyList<SentinelTypeField> Fields);

public sealed record SentinelPageTypeAssignmentView(
    Guid TypeId, string Name, string Icon, IReadOnlyList<SentinelTypeField> Fields, IReadOnlyDictionary<string, string> Values);

public sealed record SentinelTypedPageRow(Guid PageId, string Title, string? Icon, IReadOnlyDictionary<string, string> Values, DateTimeOffset UpdatedAt);

public interface ISentinelPageTypeService
{
    Task<IReadOnlyList<SentinelPageTypeView>> ListTypesAsync(string username, CancellationToken cancellationToken = default);
    Task<SentinelPageTypeView> SaveTypeAsync(SentinelPageTypeEditor editor, string username, CancellationToken cancellationToken = default);
    Task DeleteTypeAsync(Guid typeId, string username, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelPageTypeAssignmentView>> GetForPageAsync(Guid pageId, string username, CancellationToken cancellationToken = default);
    Task AssignAsync(Guid pageId, Guid typeId, string username, CancellationToken cancellationToken = default);
    Task UnassignAsync(Guid pageId, Guid typeId, string username, CancellationToken cancellationToken = default);
    // Returns the stored (normalized) value; throws ArgumentException for a value the field can't hold.
    Task<string?> SetValueAsync(Guid pageId, Guid typeId, string fieldId, string? value, string username, CancellationToken cancellationToken = default);
    // Pages with the type that the user can view, filtered (all filters must match) and sorted.
    Task<IReadOnlyList<SentinelTypedPageRow>> QueryAsync(
        Guid typeId, string username, IReadOnlyList<SentinelTypeFilter> filters, string? sortFieldId, bool descending,
        CancellationToken cancellationToken = default);
}
