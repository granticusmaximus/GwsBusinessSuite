namespace GwsBusinessSuite.Application.AdminPortal;

public sealed record AdminListViewDefinition(Guid Id, string Name, string FilterJson, IReadOnlyList<string> Columns);

public interface IAdminListViewService
{
    Task<IReadOnlyList<AdminListViewDefinition>> ListAsync(string role, string listKey, CancellationToken ct = default);
    Task<AdminListViewDefinition> SaveAsync(string role, string listKey, string name, string filterJson,
        IReadOnlyCollection<string> columns, string performedBy, CancellationToken ct = default);
    Task DeleteAsync(Guid id, string role, string listKey, CancellationToken ct = default);
}

public static class AdminListViewRules
{
    public const int MaxViewsPerList = 12;
    public const int MaxNameLength = 80;
    public const int MaxFilterJsonLength = 4000;
    public const int MaxColumnsLength = 1000;

    public static string Required(string? value, string label, int max)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) throw new ArgumentException($"{label} is required.");
        if (text.Length > max) throw new ArgumentException($"{label} must be {max} characters or fewer.");
        return text;
    }
}
