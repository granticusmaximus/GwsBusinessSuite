using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Typed pages (Sentinel item 11). Types are shared by the workspace: anyone with Sentinel access
// can create one; only its creator or an Admin can change or delete it. Giving a page a type, or
// setting its values, needs Edit on that page; listing a type's pages shows only pages the
// person can view.
public sealed class SentinelPageTypeService(IAppDbContext db, ISentinelAccessService accessService, TimeProvider timeProvider) : ISentinelPageTypeService
{
    private const int MaxTypes = 200;

    public async Task<IReadOnlyList<SentinelPageTypeView>> ListTypesAsync(string username, CancellationToken cancellationToken = default)
    {
        var types = await db.SentinelPageTypes.AsNoTracking().ToListAsync(cancellationToken);
        var counts = await db.SentinelPageTypeAssignments.AsNoTracking()
            .GroupBy(assignment => assignment.PageTypeId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Key, group => group.Count, cancellationToken);
        var isAdmin = await IsAdminAsync(username, cancellationToken);
        return types.OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(type => ToView(type, counts.GetValueOrDefault(type.Id), isAdmin || IsCreator(type, username)))
            .ToList();
    }

    public async Task<SentinelPageTypeView> SaveTypeAsync(SentinelPageTypeEditor editor, string username, CancellationToken cancellationToken = default)
    {
        var name = (editor.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException("Give the type a name of up to 60 characters.");
        var fields = SentinelPageTypes.NormalizeFields(editor.Fields ?? []);
        var normalized = SentinelPageTypes.NormalizeName(name);
        var clash = await db.SentinelPageTypes.AnyAsync(type => type.NormalizedName == normalized && type.Id != editor.Id, cancellationToken);
        if (clash) throw new ArgumentException($"There's already a type called \"{name}\".");

        SentinelPageType type;
        if (editor.Id is { } id)
        {
            type = await db.SentinelPageTypes.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new ArgumentException("That type no longer exists.");
            await RequireCanEditAsync(type, username, cancellationToken);
        }
        else
        {
            if (await db.SentinelPageTypes.CountAsync(cancellationToken) >= MaxTypes) throw new ArgumentException($"The workspace already has {MaxTypes} types.");
            type = new SentinelPageType { Name = name, NormalizedName = normalized, CreatedBy = username };
            db.SentinelPageTypes.Add(type);
        }
        type.Name = name;
        type.NormalizedName = normalized;
        var icon = (editor.Icon ?? string.Empty).Trim();
        type.Icon = icon.Length == 0 ? "🏷️" : icon[..Math.Min(icon.Length, 16)];
        var description = (editor.Description ?? string.Empty).Trim();
        type.Description = description.Length > 300 ? description[..300] : description;
        // Values of a removed field stay stored but unused; adding the field back by the same id
        // isn't possible from the UI, so they are effectively gone.
        type.FieldsJson = SentinelPageTypes.SerializeFields(fields);
        type.UpdatedAt = timeProvider.GetUtcNow();
        type.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.SentinelPageTypeAssignments.CountAsync(assignment => assignment.PageTypeId == type.Id, cancellationToken);
        return ToView(type, count, canEdit: true);
    }

    public async Task DeleteTypeAsync(Guid typeId, string username, CancellationToken cancellationToken = default)
    {
        var type = await db.SentinelPageTypes.FirstOrDefaultAsync(item => item.Id == typeId, cancellationToken)
            ?? throw new ArgumentException("That type no longer exists.");
        await RequireCanEditAsync(type, username, cancellationToken);
        var assignments = await db.SentinelPageTypeAssignments.Where(assignment => assignment.PageTypeId == typeId).ToListAsync(cancellationToken);
        db.SentinelPageTypeAssignments.RemoveRange(assignments);
        db.SentinelPageTypes.Remove(type);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelPageTypeAssignmentView>> GetForPageAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        if (!await accessService.CanAccessAsync(pageId, false, username, SentinelAccessLevels.View, cancellationToken)) return [];
        var assignments = await db.SentinelPageTypeAssignments.AsNoTracking().Where(item => item.WikiPageId == pageId).ToListAsync(cancellationToken);
        if (assignments.Count == 0) return [];
        var typeIds = assignments.Select(item => item.PageTypeId).ToList();
        var types = await db.SentinelPageTypes.AsNoTracking().Where(type => typeIds.Contains(type.Id)).ToDictionaryAsync(type => type.Id, cancellationToken);
        return assignments.Where(item => types.ContainsKey(item.PageTypeId))
            .Select(item => (Assignment: item, Type: types[item.PageTypeId]))
            .OrderBy(pair => pair.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SentinelPageTypeAssignmentView(pair.Type.Id, pair.Type.Name, pair.Type.Icon,
                SentinelPageTypes.ParseFields(pair.Type.FieldsJson), SentinelPageTypes.ParseValues(pair.Assignment.ValuesJson)))
            .ToList();
    }

    public async Task AssignAsync(Guid pageId, Guid typeId, string username, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(pageId, username, cancellationToken);
        if (!await db.SentinelPageTypes.AnyAsync(type => type.Id == typeId, cancellationToken)) throw new ArgumentException("That type no longer exists.");
        if (await db.SentinelPageTypeAssignments.AnyAsync(item => item.WikiPageId == pageId && item.PageTypeId == typeId, cancellationToken)) return;
        db.SentinelPageTypeAssignments.Add(new SentinelPageTypeAssignment { WikiPageId = pageId, PageTypeId = typeId, CreatedBy = username });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnassignAsync(Guid pageId, Guid typeId, string username, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(pageId, username, cancellationToken);
        var assignment = await db.SentinelPageTypeAssignments.FirstOrDefaultAsync(item => item.WikiPageId == pageId && item.PageTypeId == typeId, cancellationToken);
        if (assignment is null) return;
        db.SentinelPageTypeAssignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> SetValueAsync(Guid pageId, Guid typeId, string fieldId, string? value, string username, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(pageId, username, cancellationToken);
        var assignment = await db.SentinelPageTypeAssignments.FirstOrDefaultAsync(item => item.WikiPageId == pageId && item.PageTypeId == typeId, cancellationToken)
            ?? throw new ArgumentException("The page doesn't have that type any more.");
        var type = await db.SentinelPageTypes.AsNoTracking().FirstAsync(item => item.Id == typeId, cancellationToken);
        var field = SentinelPageTypes.ParseFields(type.FieldsJson).FirstOrDefault(item => item.Id == fieldId)
            ?? throw new ArgumentException("That field no longer exists.");
        var normalized = SentinelPageTypes.NormalizeValue(field, value);
        var values = new Dictionary<string, string>(SentinelPageTypes.ParseValues(assignment.ValuesJson));
        if (normalized is null) values.Remove(fieldId);
        else values[fieldId] = normalized;
        assignment.ValuesJson = SentinelPageTypes.SerializeValues(values);
        assignment.UpdatedAt = timeProvider.GetUtcNow();
        assignment.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return normalized;
    }

    public async Task<IReadOnlyList<SentinelTypedPageRow>> QueryAsync(
        Guid typeId, string username, IReadOnlyList<SentinelTypeFilter> filters, string? sortFieldId, bool descending,
        CancellationToken cancellationToken = default)
    {
        var type = await db.SentinelPageTypes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == typeId, cancellationToken);
        if (type is null) return [];
        var fields = SentinelPageTypes.ParseFields(type.FieldsJson).ToDictionary(field => field.Id);
        var assignments = await db.SentinelPageTypeAssignments.AsNoTracking().Where(item => item.PageTypeId == typeId).ToListAsync(cancellationToken);
        var pageIds = assignments.Select(item => item.WikiPageId).ToList();
        var pages = await db.WikiPages.AsNoTracking()
            .Where(page => pageIds.Contains(page.Id) && page.TrashedAt == null)
            .Select(page => new { page.Id, page.Title, page.Icon, page.CreatedAt, page.UpdatedAt })
            .ToListAsync(cancellationToken);
        var visible = await accessService.GetAccessibleTargetsAsync(
            pages.Select(page => new SentinelAccessTarget(page.Id, false)).ToList(), username, SentinelAccessLevels.View, cancellationToken);
        var valuesByPage = assignments.ToDictionary(item => item.WikiPageId, item => SentinelPageTypes.ParseValues(item.ValuesJson));

        var rows = pages.Where(page => visible.Contains(new SentinelAccessTarget(page.Id, false)))
            .Select(page => new SentinelTypedPageRow(page.Id, string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title, page.Icon,
                valuesByPage.GetValueOrDefault(page.Id) ?? new Dictionary<string, string>(), page.UpdatedAt ?? page.CreatedAt))
            .Where(row => filters.All(filter =>
                !fields.TryGetValue(filter.FieldId, out var field) || SentinelPageTypes.Matches(field, row.Values.GetValueOrDefault(filter.FieldId), filter)))
            .ToList();

        // Sorted in memory: values live in JSON, and SQLite can't order by DateTimeOffset anyway.
        if (sortFieldId is not null && fields.TryGetValue(sortFieldId, out var sortField))
        {
            rows.Sort((a, b) => SentinelPageTypes.Compare(sortField, a.Values.GetValueOrDefault(sortFieldId), b.Values.GetValueOrDefault(sortFieldId)));
            if (descending)
            {
                // Keep empty values last when reversing.
                var filled = rows.Where(row => !string.IsNullOrEmpty(row.Values.GetValueOrDefault(sortFieldId))).Reverse().ToList();
                rows = [.. filled, .. rows.Where(row => string.IsNullOrEmpty(row.Values.GetValueOrDefault(sortFieldId)))];
            }
        }
        else
        {
            rows = (descending ? rows.OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase).Reverse() : rows.OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        return rows;
    }

    private async Task RequireEditAsync(Guid pageId, string username, CancellationToken cancellationToken)
    {
        if (!await db.WikiPages.AnyAsync(page => page.Id == pageId && page.TrashedAt == null, cancellationToken))
            throw new ArgumentException("That page no longer exists.");
        if (!await accessService.CanAccessAsync(pageId, false, username, SentinelAccessLevels.Edit, cancellationToken))
            throw new ArgumentException("You don't have edit access to this page.");
    }

    private async Task RequireCanEditAsync(SentinelPageType type, string username, CancellationToken cancellationToken)
    {
        if (!IsCreator(type, username) && !await IsAdminAsync(username, cancellationToken))
            throw new ArgumentException("Only the person who created this type, or an admin, can change it.");
    }

    private static bool IsCreator(SentinelPageType type, string username) =>
        string.Equals(type.CreatedBy, username, StringComparison.OrdinalIgnoreCase);

    private Task<bool> IsAdminAsync(string username, CancellationToken cancellationToken) =>
        db.AppUsers.AnyAsync(user => user.Username == username && user.Role == AppRoles.Admin && user.IsActive, cancellationToken);

    private static SentinelPageTypeView ToView(SentinelPageType type, int count, bool canEdit) =>
        new(type.Id, type.Name, type.Icon, type.Description, SentinelPageTypes.ParseFields(type.FieldsJson), count, canEdit);
}
