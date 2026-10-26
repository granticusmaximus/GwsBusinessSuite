using System.Text.Json;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class AdminListViewService(IDbContextFactory<ApplicationDbContext> dbFactory) : IAdminListViewService
{
    public async Task<IReadOnlyList<AdminListViewDefinition>> ListAsync(string role, string listKey, CancellationToken ct = default)
    {
        role = AdminListViewRules.Required(role, "Role", 40);
        listKey = AdminListViewRules.Required(listKey, "List key", 80);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var views = await db.AdminListViews.AsNoTracking()
            .Where(view => view.Role == role && view.ListKey == listKey)
            .OrderBy(view => view.Name)
            .ToListAsync(ct);
        return views.Select(ToDefinition).ToList();
    }

    public async Task<AdminListViewDefinition> SaveAsync(string role, string listKey, string name, string filterJson,
        IReadOnlyCollection<string> columns, string performedBy, CancellationToken ct = default)
    {
        role = AdminListViewRules.Required(role, "Role", 40);
        listKey = AdminListViewRules.Required(listKey, "List key", 80);
        name = AdminListViewRules.Required(name, "View name", AdminListViewRules.MaxNameLength);
        performedBy = AdminListViewRules.Required(performedBy, "Username", 100);
        filterJson = string.IsNullOrWhiteSpace(filterJson) ? "{}" : filterJson.Trim();
        if (filterJson.Length > AdminListViewRules.MaxFilterJsonLength) throw new ArgumentException("The saved filters are too large.");
        try { JsonDocument.Parse(filterJson).Dispose(); }
        catch (JsonException) { throw new ArgumentException("The saved filters are invalid."); }

        var columnsCsv = string.Join(',', columns.Select(column => column.Trim()).Where(column => column.Length > 0).Distinct(StringComparer.Ordinal));
        if (columnsCsv.Length > AdminListViewRules.MaxColumnsLength) throw new ArgumentException("The saved columns are too large.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AdminListViews.SingleOrDefaultAsync(
            view => view.Role == role && view.ListKey == listKey && view.Name == name, ct);
        if (existing is null)
        {
            if (await db.AdminListViews.CountAsync(view => view.Role == role && view.ListKey == listKey, ct) >= AdminListViewRules.MaxViewsPerList)
                throw new ArgumentException($"A list can have up to {AdminListViewRules.MaxViewsPerList} saved views.");
            existing = new AdminListView { Role = role, ListKey = listKey, Name = name, CreatedBy = performedBy };
            db.AdminListViews.Add(existing);
        }
        existing.FilterJson = filterJson;
        existing.ColumnsCsv = columnsCsv;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.UpdatedBy = performedBy;
        await db.SaveChangesAsync(ct);
        return ToDefinition(existing);
    }

    public async Task DeleteAsync(Guid id, string role, string listKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AdminListViews.SingleOrDefaultAsync(
            view => view.Id == id && view.Role == role && view.ListKey == listKey, ct);
        if (existing is null) return;
        db.AdminListViews.Remove(existing);
        await db.SaveChangesAsync(ct);
    }

    private static AdminListViewDefinition ToDefinition(AdminListView view) => new(view.Id, view.Name, view.FilterJson,
        view.ColumnsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
