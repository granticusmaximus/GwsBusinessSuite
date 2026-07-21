using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class WikiDatabaseService(IAppDbContext dbContext) : IWikiDatabaseService
{
    public async Task<IReadOnlyList<WikiDatabase>> ListDatabasesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.WikiDatabases.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<WikiDatabase?> GetDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default) =>
        await dbContext.WikiDatabases.AsNoTracking()
            .Include(item => item.Properties)
            .Include(item => item.Rows)
            .Include(item => item.Views)
            .FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken);

    public async Task<WikiDatabase> CreateDatabaseAsync(
        string title,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var siblingOrders = await dbContext.WikiDatabases
            .Where(item => item.ParentWikiPageId == parentWikiPageId)
            .Select(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        var database = new WikiDatabase
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled Database" : title.Trim(),
            ParentWikiPageId = parentWikiPageId,
            SortOrder = siblingOrders.Count == 0 ? 0 : siblingOrders.Max() + 1,
            CreatedAt = now,
            CreatedBy = performedBy
        };

        // Every database starts with a Title property (the primary label column) and one
        // default Table view - matches Notion's own "every database has a Name column and
        // starts in table view" default.
        var titleProperty = new WikiDatabaseProperty
        {
            WikiDatabase = database,
            Name = "Name",
            Type = WikiDatabasePropertyTypes.Title,
            SortOrder = 0,
            CreatedAt = now,
            CreatedBy = performedBy
        };
        database.Properties.Add(titleProperty);
        database.Views.Add(new WikiDatabaseView
        {
            WikiDatabase = database,
            Name = "Table",
            Type = WikiDatabaseViewTypes.Table,
            SortOrder = 0,
            CreatedAt = now,
            CreatedBy = performedBy
        });

        await dbContext.WikiDatabases.AddAsync(database, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return database;
    }

    public async Task<WikiDatabase> RenameDatabaseAsync(
        Guid wikiDatabaseId,
        string title,
        string? icon,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");

        database.Title = string.IsNullOrWhiteSpace(title) ? database.Title : title.Trim();
        database.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        database.UpdatedAt = DateTimeOffset.UtcNow;
        database.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
        return database;
    }

    public async Task DeleteDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken);
        if (database is null)
        {
            return;
        }

        // Properties/Rows/Views cascade-delete via the FKs configured in ApplicationDbContext.
        dbContext.WikiDatabases.Remove(database);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderDatabaseAsync(
        Guid wikiDatabaseId,
        Guid? newParentWikiPageId,
        int newSortOrder,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken)
            ?? throw new InvalidOperationException("The database no longer exists.");

        var siblings = await dbContext.WikiDatabases
            .Where(item => item.ParentWikiPageId == newParentWikiPageId && item.Id != wikiDatabaseId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        siblings.Insert(Math.Clamp(newSortOrder, 0, siblings.Count), database);

        var now = DateTimeOffset.UtcNow;
        database.ParentWikiPageId = newParentWikiPageId;
        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].SortOrder = index;
            siblings[index].UpdatedAt = now;
            siblings[index].UpdatedBy = performedBy;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WikiDatabaseProperty> SavePropertyAsync(
        Guid wikiDatabaseId,
        WikiDatabasePropertyEditor editor,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (string.IsNullOrWhiteSpace(editor.Name))
        {
            throw new ArgumentException("Property name is required.", nameof(editor));
        }

        var now = DateTimeOffset.UtcNow;
        var property = editor.Id is { } propertyId
            ? await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(item => item.Id == propertyId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken)
                ?? throw new KeyNotFoundException("The property no longer exists.")
            : null;

        // Exactly one Title property per database - it's the primary label every row,
        // Board card, and Gallery card is keyed on.
        if (property?.Type != WikiDatabasePropertyTypes.Title && editor.Type == WikiDatabasePropertyTypes.Title)
        {
            var alreadyHasTitle = await dbContext.WikiDatabaseProperties.AnyAsync(
                item => item.WikiDatabaseId == wikiDatabaseId && item.Type == WikiDatabasePropertyTypes.Title, cancellationToken);
            if (alreadyHasTitle)
            {
                throw new InvalidOperationException("This database already has a Title property.");
            }
        }

        var isNew = property is null;
        property ??= new WikiDatabaseProperty
        {
            WikiDatabaseId = wikiDatabaseId,
            Name = editor.Name.Trim(),
            Type = editor.Type,
            CreatedAt = now,
            CreatedBy = performedBy,
            SortOrder = await NextPropertySortOrderAsync(wikiDatabaseId, cancellationToken)
        };

        property.Name = editor.Name.Trim();
        // Type is immutable once created - changing it would strand PropertyValuesJson
        // entries authored under the old type's shape (a select's option-id string vs. a
        // number's decimal, for example) with no safe reinterpretation.
        if (!isNew && property.Type != editor.Type)
        {
            throw new InvalidOperationException("A property's type can't be changed after creation - delete and re-add it instead.");
        }
        property.ConfigJson = editor.Type is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.MultiSelect
            ? WikiDatabasePropertyConfig.Serialize(editor.Options)
            : "{}";
        property.UpdatedAt = now;
        property.UpdatedBy = performedBy;

        if (isNew)
        {
            await dbContext.WikiDatabaseProperties.AddAsync(property, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return property;
    }

    public async Task DeletePropertyAsync(Guid wikiDatabaseId, Guid propertyId, string performedBy, CancellationToken cancellationToken = default)
    {
        var property = await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(
            item => item.Id == propertyId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (property is null)
        {
            return;
        }

        if (property.Type == WikiDatabasePropertyTypes.Title)
        {
            throw new InvalidOperationException("The Title property can't be deleted.");
        }

        dbContext.WikiDatabaseProperties.Remove(property);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WikiDatabaseRow> SaveRowAsync(
        Guid wikiDatabaseId,
        WikiDatabaseRowEditor editor,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        var row = editor.Id is { } rowId
            ? await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken)
                ?? throw new KeyNotFoundException("The row no longer exists.")
            : null;

        var isNew = row is null;
        row ??= new WikiDatabaseRow
        {
            WikiDatabaseId = wikiDatabaseId,
            CreatedAt = now,
            CreatedBy = performedBy,
            SortOrder = await NextRowSortOrderAsync(wikiDatabaseId, cancellationToken)
        };

        var values = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in editor.Values)
        {
            values[key] = value?.DeepClone();
        }
        row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
        if (editor.BlocksJson is not null)
        {
            row.BlocksJson = string.IsNullOrWhiteSpace(editor.BlocksJson) ? "[]" : editor.BlocksJson;
        }
        row.UpdatedAt = now;
        row.UpdatedBy = performedBy;

        if (isNew)
        {
            await dbContext.WikiDatabaseRows.AddAsync(row, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task DeleteRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (row is null)
        {
            return;
        }

        dbContext.WikiDatabaseRows.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveRowAsync(
        Guid wikiDatabaseId,
        Guid rowId,
        Guid groupByPropertyId,
        string? newGroupOptionId,
        int newSortOrder,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken)
            ?? throw new InvalidOperationException("The row no longer exists.");

        var otherRows = await dbContext.WikiDatabaseRows
            .Where(item => item.WikiDatabaseId == wikiDatabaseId && item.Id != rowId)
            .ToListAsync(cancellationToken);
        var normalizedTarget = newGroupOptionId ?? string.Empty;
        var siblingsInTargetGroup = otherRows
            .Where(item => (WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(item.PropertyValuesJson), groupByPropertyId) ?? string.Empty) == normalizedTarget)
            .OrderBy(item => item.SortOrder)
            .ToList();
        siblingsInTargetGroup.Insert(Math.Clamp(newSortOrder, 0, siblingsInTargetGroup.Count), row);

        var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
        WikiPropertyValues.SetText(values, groupByPropertyId, newGroupOptionId);
        row.PropertyValuesJson = WikiPropertyValues.Serialize(values);

        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < siblingsInTargetGroup.Count; index++)
        {
            siblingsInTargetGroup[index].SortOrder = index;
            siblingsInTargetGroup[index].UpdatedAt = now;
            siblingsInTargetGroup[index].UpdatedBy = performedBy;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WikiDatabaseView> SaveViewAsync(
        Guid wikiDatabaseId,
        Guid? viewId,
        string name,
        string type,
        WikiDatabaseViewConfig config,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var view = viewId is { } id
            ? await dbContext.WikiDatabaseViews.FirstOrDefaultAsync(item => item.Id == id && item.WikiDatabaseId == wikiDatabaseId, cancellationToken)
                ?? throw new KeyNotFoundException("The view no longer exists.")
            : null;

        var isNew = view is null;
        view ??= new WikiDatabaseView
        {
            WikiDatabaseId = wikiDatabaseId,
            Name = name,
            Type = type,
            CreatedAt = now,
            CreatedBy = performedBy,
            SortOrder = await NextViewSortOrderAsync(wikiDatabaseId, cancellationToken)
        };

        view.Name = string.IsNullOrWhiteSpace(name) ? view.Name : name.Trim();
        view.ConfigJson = WikiDatabaseViewConfigJson.Serialize(config);
        view.UpdatedAt = now;
        view.UpdatedBy = performedBy;

        if (isNew)
        {
            await dbContext.WikiDatabaseViews.AddAsync(view, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return view;
    }

    public async Task DeleteViewAsync(Guid wikiDatabaseId, Guid viewId, string performedBy, CancellationToken cancellationToken = default)
    {
        var remainingCount = await dbContext.WikiDatabaseViews.CountAsync(item => item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (remainingCount <= 1)
        {
            throw new InvalidOperationException("A database needs at least one view.");
        }

        var view = await dbContext.WikiDatabaseViews.FirstOrDefaultAsync(item => item.Id == viewId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (view is null)
        {
            return;
        }

        dbContext.WikiDatabaseViews.Remove(view);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> NextPropertySortOrderAsync(Guid wikiDatabaseId, CancellationToken cancellationToken)
    {
        var orders = await dbContext.WikiDatabaseProperties.Where(item => item.WikiDatabaseId == wikiDatabaseId).Select(item => item.SortOrder).ToListAsync(cancellationToken);
        return orders.Count == 0 ? 0 : orders.Max() + 1;
    }

    private async Task<int> NextRowSortOrderAsync(Guid wikiDatabaseId, CancellationToken cancellationToken)
    {
        var orders = await dbContext.WikiDatabaseRows.Where(item => item.WikiDatabaseId == wikiDatabaseId).Select(item => item.SortOrder).ToListAsync(cancellationToken);
        return orders.Count == 0 ? 0 : orders.Max() + 1;
    }

    private async Task<int> NextViewSortOrderAsync(Guid wikiDatabaseId, CancellationToken cancellationToken)
    {
        var orders = await dbContext.WikiDatabaseViews.Where(item => item.WikiDatabaseId == wikiDatabaseId).Select(item => item.SortOrder).ToListAsync(cancellationToken);
        return orders.Count == 0 ? 0 : orders.Max() + 1;
    }
}
