using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.Services;

// automationTriggerService is optional (defaults to null, treated as no-op below) purely so
// the three dozen existing WikiDatabaseServiceTests/SentinelWorkspaceServiceTests/
// SentinelTemplateServiceTests call sites that construct this service directly - none of
// which exercise database automations - don't all need a fake automation dependency graph
// wired through them. Production DI always resolves the real registered instance.
public sealed class WikiDatabaseService(IAppDbContext dbContext, IAutomationTriggerService? automationTriggerService = null) : IWikiDatabaseService
{
    // Same bound as WikiService.MaxRevisionsPerPage - kept as an independent constant rather
    // than shared so each history table can be tuned separately later if needed.
    private const int MaxRevisionsPerRow = 20;
    private const int MaxCsvHeaderCharacters = 120;
    private const int MaxCsvFieldCharacters = 128 * 1024;
    private const int MaxCsvWarnings = 200;

    public async Task<IReadOnlyList<WikiDatabase>> ListDatabasesAsync(bool includeTrashed = false, CancellationToken cancellationToken = default)
    {
        var query = dbContext.WikiDatabases.AsNoTracking();
        if (!includeTrashed)
        {
            query = query.Where(database => database.TrashedAt == null);
        }
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WikiDatabase>> ListTrashedDatabasesAsync(CancellationToken cancellationToken = default)
    {
        var databases = await dbContext.WikiDatabases
            .AsNoTracking()
            .Where(database => database.TrashedAt != null)
            .ToListAsync(cancellationToken);

        // SQLite can't translate ORDER BY on a DateTimeOffset column - order client-side
        // after materializing (same pattern used throughout this app).
        return databases.OrderByDescending(database => database.TrashedAt).ToList();
    }

    public async Task<WikiDatabase?> GetDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.AsNoTracking()
            .Include(item => item.Properties)
            .Include(item => item.Rows.Where(row => row.TrashedAt == null))
            .Include(item => item.Views)
            .FirstOrDefaultAsync(item => item.Id == wikiDatabaseId && item.TrashedAt == null, cancellationToken);
        if (database is null)
        {
            return null;
        }

        var relatedDatabases = await LoadRelatedDatabasesTransitivelyAsync(database, cancellationToken);

        WikiDatabaseComputation.Materialize(database, relatedDatabases);
        return database;
    }

    // A rollup can aggregate another database's own Rollup property (rollup-of-a-rollup), which
    // needs THAT database's own related databases resolved too - so this has to keep following
    // Relation properties transitively, not stop at directly-related databases, or a two-hop
    // chain fails to resolve with "#REF! Related database missing". visitedDatabaseIds also
    // protects against a relation cycle (A relates to B, B relates back to A) turning this into
    // an infinite fetch loop.
    private async Task<Dictionary<Guid, WikiDatabase>> LoadRelatedDatabasesTransitivelyAsync(
        WikiDatabase database, CancellationToken cancellationToken)
    {
        var visitedDatabaseIds = new HashSet<Guid> { database.Id };
        var relatedDatabases = new Dictionary<Guid, WikiDatabase>();
        var frontier = RelatedDatabaseIds(database, visitedDatabaseIds);

        while (frontier.Count > 0)
        {
            var batch = await dbContext.WikiDatabases.AsNoTracking()
                .Where(item => frontier.Contains(item.Id) && item.TrashedAt == null)
                .Include(item => item.Properties)
                .Include(item => item.Rows.Where(row => row.TrashedAt == null))
                .ToListAsync(cancellationToken);
            foreach (var related in batch)
            {
                relatedDatabases[related.Id] = related;
            }

            frontier = batch
                .SelectMany(related => RelatedDatabaseIds(related, visitedDatabaseIds))
                .Distinct()
                .ToList();
        }

        return relatedDatabases;
    }

    private static List<Guid> RelatedDatabaseIds(WikiDatabase database, HashSet<Guid> visitedDatabaseIds) =>
        database.Properties
            .Where(property => property.Type == WikiDatabasePropertyTypes.Relation)
            .Select(property => WikiDatabasePropertyConfig.Parse(property).RelatedDatabaseId)
            .Where(id => id.HasValue && visitedDatabaseIds.Add(id.Value))
            .Select(id => id!.Value)
            .ToList();

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

    public async Task<WikiDatabase> DuplicateDatabaseAsync(
        Guid wikiDatabaseId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var source = await GetDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
        var now = DateTimeOffset.UtcNow;
        var propertyIds = source.Properties.ToDictionary(property => property.Id, _ => Guid.NewGuid());
        var rowIds = source.Rows.ToDictionary(row => row.Id, _ => Guid.NewGuid());
        var sourceRowTemplates = await dbContext.WikiDatabaseRowTemplates.AsNoTracking()
            .Where(template => template.WikiDatabaseId == source.Id)
            .ToListAsync(cancellationToken);

        var duplicate = new WikiDatabase
        {
            Id = Guid.NewGuid(),
            Title = $"{source.Title} Copy",
            Icon = source.Icon,
            ParentWikiPageId = source.ParentWikiPageId,
            SortOrder = source.SortOrder + 1,
            CreatedAt = now,
            CreatedBy = performedBy
        };

        foreach (var property in source.Properties.OrderBy(property => property.SortOrder))
        {
            duplicate.Properties.Add(new WikiDatabaseProperty
            {
                Id = propertyIds[property.Id],
                WikiDatabaseId = duplicate.Id,
                Name = property.Name,
                Type = property.Type,
                SortOrder = property.SortOrder,
                ConfigJson = RemapPropertyConfig(property.ConfigJson, property.Type, propertyIds, source.Id, duplicate.Id),
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

        foreach (var row in source.Rows.OrderBy(row => row.SortOrder))
        {
            var remappedValues = RemapPropertyValues(
                WikiPropertyValues.ParseObject(row.PropertyValuesJson), source.Properties, propertyIds, rowIds, source.Id);

            var blocks = WikiBlockJson.ParseBlocks(row.BlocksJson)
                .Select(block => block with { Id = Guid.NewGuid() })
                .ToList();
            duplicate.Rows.Add(new WikiDatabaseRow
            {
                Id = rowIds[row.Id],
                WikiDatabaseId = duplicate.Id,
                ParentRowId = row.ParentRowId is { } parentRowId && rowIds.TryGetValue(parentRowId, out var remappedParentRowId)
                    ? remappedParentRowId
                    : null,
                SortOrder = row.SortOrder,
                PropertyValuesJson = WikiPropertyValues.Serialize(remappedValues),
                BlocksJson = WikiBlockJson.Serialize(blocks),
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

        foreach (var template in sourceRowTemplates)
        {
            var remappedDefaults = RemapPropertyValues(
                WikiPropertyValues.ParseObject(template.DefaultPropertyValuesJson),
                source.Properties,
                propertyIds,
                rowIds,
                source.Id);
            var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson)
                .Select(block => block with { Id = Guid.NewGuid() })
                .ToList();
            duplicate.RowTemplates.Add(new WikiDatabaseRowTemplate
            {
                Id = Guid.NewGuid(),
                WikiDatabaseId = duplicate.Id,
                Name = template.Name,
                NormalizedName = template.NormalizedName,
                BlocksJson = WikiBlockJson.Serialize(blocks),
                DefaultPropertyValuesJson = WikiPropertyValues.Serialize(remappedDefaults),
                Icon = template.Icon,
                CoverImageUrl = template.CoverImageUrl,
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

        foreach (var view in source.Views.OrderBy(view => view.SortOrder))
        {
            var config = WikiDatabaseViewConfigJson.Parse(view.ConfigJson);
            duplicate.Views.Add(new WikiDatabaseView
            {
                Id = Guid.NewGuid(),
                WikiDatabaseId = duplicate.Id,
                Name = view.Name,
                Type = view.Type,
                SortOrder = view.SortOrder,
                ConfigJson = WikiDatabaseViewConfigJson.Serialize(new WikiDatabaseViewConfig(
                    config.Filters.Select(filter => filter with { PropertyId = RemapPropertyId(filter.PropertyId, propertyIds) }).ToList(),
                    config.Sorts.Select(sort => sort with { PropertyId = RemapPropertyId(sort.PropertyId, propertyIds) }).ToList(),
                    config.GroupByPropertyId is null ? null : RemapPropertyId(config.GroupByPropertyId, propertyIds),
                    config.OpenPageMode,
                    (config.PagePropertyOrder ?? []).Select(propertyId => RemapPropertyId(propertyId, propertyIds)).ToList(),
                    (config.HiddenPagePropertyIds ?? []).Select(propertyId => RemapPropertyId(propertyId, propertyIds)).ToList(),
                    (config.Calculations ?? new Dictionary<string, string>())
                        .ToDictionary(item => RemapPropertyId(item.Key, propertyIds), item => item.Value),
                    FilterGroup: RemapFilterGroup(config.FilterGroup, propertyIds),
                    DependencyPropertyId: config.DependencyPropertyId is null
                        ? null
                        : RemapPropertyId(config.DependencyPropertyId, propertyIds))),
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

        var followingSiblings = await dbContext.WikiDatabases
            .Where(database => database.ParentWikiPageId == source.ParentWikiPageId && database.SortOrder > source.SortOrder)
            .ToListAsync(cancellationToken);
        foreach (var sibling in followingSiblings)
        {
            sibling.SortOrder++;
            sibling.UpdatedAt = now;
            sibling.UpdatedBy = performedBy;
        }

        await dbContext.WikiDatabases.AddAsync(duplicate, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return duplicate;
    }

    private static string RemapPropertyId(string propertyId, IReadOnlyDictionary<Guid, Guid> propertyIds) =>
        Guid.TryParse(propertyId, out var parsed) && propertyIds.TryGetValue(parsed, out var remapped)
            ? remapped.ToString()
            : propertyId;

    private static WikiDatabaseFilterGroup? RemapFilterGroup(
        WikiDatabaseFilterGroup? group,
        IReadOnlyDictionary<Guid, Guid> propertyIds) => group is null
            ? null
            : new WikiDatabaseFilterGroup(
                group.Combinator,
                group.Conditions.Select(condition => condition with
                {
                    PropertyId = RemapPropertyId(condition.PropertyId, propertyIds)
                }).ToList(),
                group.Groups.Select(child => RemapFilterGroup(child, propertyIds)!).ToList());

    private static string RemapPropertyConfig(
        string configJson,
        string propertyType,
        IReadOnlyDictionary<Guid, Guid> propertyIds,
        Guid sourceDatabaseId,
        Guid targetDatabaseId)
    {
        if (propertyType is not (WikiDatabasePropertyTypes.Relation or WikiDatabasePropertyTypes.Rollup))
        {
            return configJson;
        }
        var config = WikiDatabasePropertyConfig.Parse(configJson);
        if (propertyType == WikiDatabasePropertyTypes.Relation)
        {
            config = config with
            {
                Options = [],
                RelatedDatabaseId = config.RelatedDatabaseId == sourceDatabaseId ? targetDatabaseId : config.RelatedDatabaseId,
                ReciprocalPropertyId = config.RelatedDatabaseId == sourceDatabaseId
                    && config.ReciprocalPropertyId is { } reciprocalId
                    && propertyIds.TryGetValue(reciprocalId, out var remappedReciprocalId)
                        ? remappedReciprocalId
                        : null
            };
        }
        else if (propertyType == WikiDatabasePropertyTypes.Rollup)
        {
            config = config with
            {
                RelationPropertyId = config.RelationPropertyId is { } relationId && propertyIds.TryGetValue(relationId, out var remappedRelationId)
                    ? remappedRelationId : config.RelationPropertyId,
                RollupPropertyId = config.RollupPropertyId is { } rollupId && propertyIds.TryGetValue(rollupId, out var remappedRollupId)
                    ? remappedRollupId : config.RollupPropertyId
            };
        }
        return WikiDatabasePropertyConfig.Serialize(config);
    }

    private static JsonObject RemapPropertyValues(
        JsonObject values,
        IEnumerable<WikiDatabaseProperty> properties,
        IReadOnlyDictionary<Guid, Guid> propertyIds,
        IReadOnlyDictionary<Guid, Guid> rowIds,
        Guid sourceDatabaseId)
    {
        var remappedValues = new JsonObject();
        foreach (var property in properties)
        {
            if (!propertyIds.TryGetValue(property.Id, out var newPropertyId)
                || property.Type is WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup
                || values[property.Id.ToString()] is not { } value)
            {
                continue;
            }

            if (property.Type == WikiDatabasePropertyTypes.Relation
                && WikiDatabasePropertyConfig.Parse(property).RelatedDatabaseId == sourceDatabaseId
                && value is JsonArray relationIds)
            {
                remappedValues[newPropertyId.ToString()] = new JsonArray(relationIds
                    .Select(item => Guid.TryParse(item?.GetValue<string>(), out var rowId) && rowIds.TryGetValue(rowId, out var remappedRowId)
                        ? (JsonNode)remappedRowId.ToString()
                        : item?.DeepClone())
                    .Where(item => item is not null)
                    .ToArray());
            }
            else
            {
                remappedValues[newPropertyId.ToString()] = value.DeepClone();
            }
        }
        return remappedValues;
    }

    public async Task<WikiDatabaseTemplateSnapshot> CreateTemplateSnapshotAsync(
        Guid wikiDatabaseId,
        CancellationToken cancellationToken = default)
    {
        var source = await GetDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");

        return new WikiDatabaseTemplateSnapshot(
            source.Title,
            source.Icon,
            source.Properties.OrderBy(item => item.SortOrder)
                .Select(item => new WikiDatabaseTemplateProperty(
                    item.Id, item.Name, item.Type, item.SortOrder, item.ConfigJson))
                .ToList(),
            source.Rows.OrderBy(item => item.SortOrder)
                .Select(item => new WikiDatabaseTemplateRow(
                    item.Id, item.SortOrder, item.PropertyValuesJson, item.BlocksJson))
                .ToList(),
            source.Views.OrderBy(item => item.SortOrder)
                .Select(item => new WikiDatabaseTemplateView(
                    item.Id, item.Name, item.Type, item.SortOrder, item.ConfigJson))
                .ToList())
        {
            SourceDatabaseId = source.Id
        };
    }

    public async Task<WikiDatabase> CreateDatabaseFromTemplateAsync(
        WikiDatabaseTemplateSnapshot snapshot,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Properties.Count(property => property.Type == WikiDatabasePropertyTypes.Title) != 1)
        {
            throw new InvalidOperationException("A database template must contain exactly one title property.");
        }
        if (snapshot.Views.Count == 0)
        {
            throw new InvalidOperationException("A database template must contain at least one view.");
        }

        var siblingOrders = await dbContext.WikiDatabases
            .Where(item => item.ParentWikiPageId == parentWikiPageId)
            .Select(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var propertyIds = snapshot.Properties.ToDictionary(property => property.Id, _ => Guid.NewGuid());
        var rowIds = snapshot.Rows.ToDictionary(row => row.Id, _ => Guid.NewGuid());
        var database = new WikiDatabase
        {
            Id = Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Untitled Database" : snapshot.Title.Trim(),
            Icon = snapshot.Icon,
            ParentWikiPageId = parentWikiPageId,
            SortOrder = siblingOrders.Count == 0 ? 0 : siblingOrders.Max() + 1,
            CreatedAt = now,
            CreatedBy = performedBy
        };

        foreach (var property in snapshot.Properties.OrderBy(item => item.SortOrder))
        {
            database.Properties.Add(new WikiDatabaseProperty
            {
                Id = propertyIds[property.Id],
                WikiDatabaseId = database.Id,
                Name = property.Name,
                Type = property.Type,
                SortOrder = property.SortOrder,
                ConfigJson = RemapPropertyConfig(
                    property.ConfigJson, property.Type, propertyIds, snapshot.SourceDatabaseId ?? Guid.Empty, database.Id),
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

        foreach (var row in snapshot.Rows.OrderBy(item => item.SortOrder))
        {
            var remappedValues = new JsonObject();
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            foreach (var property in snapshot.Properties)
            {
                if (property.Type is WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup
                    || values[property.Id.ToString()] is not { } value)
                {
                    continue;
                }
                var newPropertyId = propertyIds[property.Id];
                var config = WikiDatabasePropertyConfig.Parse(property.ConfigJson);
                if (property.Type == WikiDatabasePropertyTypes.Relation
                    && value is JsonArray relationIds
                    && config.RelatedDatabaseId == snapshot.SourceDatabaseId)
                {
                    remappedValues[newPropertyId.ToString()] = new JsonArray(relationIds
                        .Select(item => (JsonNode)rowIds[Guid.Parse(item!.GetValue<string>())].ToString())
                        .ToArray());
                }
                else
                {
                    remappedValues[newPropertyId.ToString()] = value.DeepClone();
                }
            }

            var blocks = WikiBlockJson.ParseBlocks(row.BlocksJson)
                .Select(block => block with { Id = Guid.NewGuid() })
                .ToList();
            database.Rows.Add(new WikiDatabaseRow
            {
                Id = rowIds[row.Id],
                WikiDatabaseId = database.Id,
                SortOrder = row.SortOrder,
                PropertyValuesJson = WikiPropertyValues.Serialize(remappedValues),
                BlocksJson = WikiBlockJson.Serialize(blocks),
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

        foreach (var view in snapshot.Views.OrderBy(item => item.SortOrder))
        {
            var config = WikiDatabaseViewConfigJson.Parse(view.ConfigJson);
            database.Views.Add(new WikiDatabaseView
            {
                Id = Guid.NewGuid(),
                WikiDatabaseId = database.Id,
                Name = view.Name,
                Type = view.Type,
                SortOrder = view.SortOrder,
                ConfigJson = WikiDatabaseViewConfigJson.Serialize(new WikiDatabaseViewConfig(
                    config.Filters.Select(filter => filter with
                    {
                        PropertyId = RemapPropertyId(filter.PropertyId, propertyIds)
                    }).ToList(),
                    config.Sorts.Select(sort => sort with
                    {
                        PropertyId = RemapPropertyId(sort.PropertyId, propertyIds)
                    }).ToList(),
                    config.GroupByPropertyId is null
                        ? null
                        : RemapPropertyId(config.GroupByPropertyId, propertyIds),
                    config.OpenPageMode,
                    (config.PagePropertyOrder ?? []).Select(propertyId => RemapPropertyId(propertyId, propertyIds)).ToList(),
                    (config.HiddenPagePropertyIds ?? []).Select(propertyId => RemapPropertyId(propertyId, propertyIds)).ToList(),
                    (config.Calculations ?? new Dictionary<string, string>())
                        .ToDictionary(item => RemapPropertyId(item.Key, propertyIds), item => item.Value),
                    FilterGroup: RemapFilterGroup(config.FilterGroup, propertyIds),
                    DependencyPropertyId: config.DependencyPropertyId is null
                        ? null
                        : RemapPropertyId(config.DependencyPropertyId, propertyIds))),
                CreatedAt = now,
                CreatedBy = performedBy
            });
        }

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
        EnsureDatabaseUnlocked(database);

        database.Title = string.IsNullOrWhiteSpace(title) ? database.Title : title.Trim();
        database.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        database.UpdatedAt = DateTimeOffset.UtcNow;
        database.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
        return database;
    }

    public async Task<WikiDatabase> SetDatabaseLockAsync(
        Guid wikiDatabaseId,
        bool isLocked,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(
            item => item.Id == wikiDatabaseId && item.TrashedAt == null,
            cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
        if (database.IsLocked == isLocked)
        {
            return database;
        }

        database.IsLocked = isLocked;
        database.UpdatedAt = DateTimeOffset.UtcNow;
        database.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
        return database;
    }

    private static void EnsureDatabaseUnlocked(WikiDatabase database)
    {
        if (database.IsLocked)
        {
            throw new InvalidOperationException(
                "Database structure is locked. Unlock it before changing metadata, properties, shared views, or row templates.");
        }
    }

    private async Task EnsureDatabaseUnlockedAsync(Guid wikiDatabaseId, CancellationToken cancellationToken)
    {
        var database = await dbContext.WikiDatabases.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wikiDatabaseId && item.TrashedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
        EnsureDatabaseUnlocked(database);
    }

    public async Task TrashDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken);
        if (database is null || database.TrashedAt is not null)
        {
            return;
        }

        // Deliberately does not touch rows, reciprocal relation properties, or Sentinel access
        // rows the way permanent delete does below - trash is reversible, so nothing else in
        // the schema should be disturbed by it.
        database.TrashedAt = DateTimeOffset.UtcNow;
        database.UpdatedAt = database.TrashedAt;
        database.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken);
        if (database is null || database.TrashedAt is null)
        {
            return;
        }

        // Same reparent-to-root safety net as WikiService.RestorePageAsync, so a database
        // whose parent page is still trashed (or gone) doesn't come back invisible.
        if (database.ParentWikiPageId is { } parentId)
        {
            var parentIsAvailable = await dbContext.WikiPages.AsNoTracking()
                .AnyAsync(item => item.Id == parentId && item.TrashedAt == null, cancellationToken);
            if (!parentIsAvailable)
            {
                database.ParentWikiPageId = null;
            }
        }

        database.TrashedAt = null;
        database.UpdatedAt = DateTimeOffset.UtcNow;
        database.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDatabasePermanentlyAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken);
        if (database is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var relationProperties = await dbContext.WikiDatabaseProperties
            .Where(property => property.WikiDatabaseId == wikiDatabaseId
                && property.Type == WikiDatabasePropertyTypes.Relation)
            .ToListAsync(cancellationToken);
        foreach (var relationProperty in relationProperties)
        {
            var relationConfig = WikiDatabasePropertyConfig.Parse(relationProperty);
            if (relationConfig.RelatedDatabaseId == wikiDatabaseId)
            {
                continue;
            }
            await RemoveReciprocalPropertyAsync(
                relationConfig.ReciprocalPropertyId,
                relationProperty.Id, now, performedBy, cancellationToken);
        }

        // Properties/Rows/Views cascade-delete via the FKs configured in ApplicationDbContext.
        // SentinelResourcePermissions/SentinelPublicShares reference this database
        // polymorphically via TargetId+IsDatabase (see WikiService.DeletePageAsync's own
        // version of this same cleanup for a WikiPage), so a real FK isn't possible here either.
        await RemoveSentinelAccessRowsAsync(wikiDatabaseId, isDatabase: true, cancellationToken);

        dbContext.WikiDatabases.Remove(database);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveSentinelAccessRowsAsync(Guid targetId, bool isDatabase, CancellationToken cancellationToken)
    {
        var permissions = await dbContext.SentinelResourcePermissions
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .ToListAsync(cancellationToken);
        if (permissions.Count > 0)
        {
            dbContext.SentinelResourcePermissions.RemoveRange(permissions);
        }

        var shares = await dbContext.SentinelPublicShares
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .ToListAsync(cancellationToken);
        if (shares.Count > 0)
        {
            dbContext.SentinelPublicShares.RemoveRange(shares);
        }
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
        await EnsureDatabaseUnlockedAsync(wikiDatabaseId, cancellationToken);
        if (string.IsNullOrWhiteSpace(editor.Name))
        {
            throw new ArgumentException("Property name is required.", nameof(editor));
        }

        var now = DateTimeOffset.UtcNow;
        var property = editor.Id is { } propertyId
            ? await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(item => item.Id == propertyId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken)
                ?? throw new KeyNotFoundException("The property no longer exists.")
            : null;
        var previousConfiguration = property is null
            ? WikiDatabasePropertyConfiguration.Empty
            : WikiDatabasePropertyConfig.Parse(property);
        var previousName = property?.Name;

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
        var configuration = new WikiDatabasePropertyConfiguration(
            editor.Type is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Status
                ? editor.Options : [],
            string.IsNullOrWhiteSpace(editor.FormulaExpression) ? null : editor.FormulaExpression.Trim(),
            editor.RelatedDatabaseId,
            editor.ReciprocalPropertyId,
            editor.RelationPropertyId,
            editor.RollupPropertyId,
            string.IsNullOrWhiteSpace(editor.RollupAggregation) ? null : editor.RollupAggregation,
            editor.Type == WikiDatabasePropertyTypes.Button ? editor.AutomationWorkflowId : null,
            editor.Type == WikiDatabasePropertyTypes.Button && !string.IsNullOrWhiteSpace(editor.ButtonLabel) ? editor.ButtonLabel.Trim() : null,
            editor.Type == WikiDatabasePropertyTypes.UniqueId && !string.IsNullOrWhiteSpace(editor.UniqueIdPrefix) ? editor.UniqueIdPrefix.Trim() : null);
        await ValidatePropertyConfigurationAsync(wikiDatabaseId, property.Id, editor.Type, configuration, cancellationToken);
        if (!isNew && editor.Type == WikiDatabasePropertyTypes.Relation
            && previousConfiguration.RelatedDatabaseId != configuration.RelatedDatabaseId)
        {
            await RemoveReciprocalPropertyAsync(
                previousConfiguration.ReciprocalPropertyId, property.Id, now, performedBy, cancellationToken);
            configuration = configuration with { ReciprocalPropertyId = null };
            var rows = await dbContext.WikiDatabaseRows.Where(row => row.WikiDatabaseId == wikiDatabaseId).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                values.Remove(property.Id.ToString());
                row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
                row.UpdatedAt = now;
                row.UpdatedBy = performedBy;
            }
        }
        property.UpdatedAt = now;
        property.UpdatedBy = performedBy;

        if (!isNew && !string.Equals(previousName, property.Name, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previousName))
        {
            var formulaProperties = await dbContext.WikiDatabaseProperties
                .Where(item => item.WikiDatabaseId == wikiDatabaseId && item.Type == WikiDatabasePropertyTypes.Formula)
                .ToListAsync(cancellationToken);
            foreach (var formulaProperty in formulaProperties)
            {
                var formulaConfig = WikiDatabasePropertyConfig.Parse(formulaProperty);
                if (string.IsNullOrWhiteSpace(formulaConfig.FormulaExpression)) continue;
                var updatedExpression = Regex.Replace(
                    formulaConfig.FormulaExpression,
                    $@"\[{Regex.Escape(previousName)}\]",
                    $"[{property.Name}]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (updatedExpression == formulaConfig.FormulaExpression) continue;
                formulaProperty.ConfigJson = WikiDatabasePropertyConfig.Serialize(formulaConfig with { FormulaExpression = updatedExpression });
                formulaProperty.UpdatedAt = now;
                formulaProperty.UpdatedBy = performedBy;
            }
        }

        if (isNew)
        {
            await dbContext.WikiDatabaseProperties.AddAsync(property, cancellationToken);
        }

        if (editor.Type == WikiDatabasePropertyTypes.Relation)
        {
            if (editor.ReciprocalRelationEnabled == false)
            {
                await RemoveReciprocalPropertyAsync(
                    previousConfiguration.ReciprocalPropertyId ?? configuration.ReciprocalPropertyId,
                    property.Id, now, performedBy, cancellationToken);
                configuration = configuration with { ReciprocalPropertyId = null };
            }
            else if (editor.ReciprocalRelationEnabled == true)
            {
                var reciprocalId = await EnsureReciprocalPropertyAsync(
                    wikiDatabaseId, property, configuration, editor.ReciprocalPropertyName,
                    now, performedBy, cancellationToken);
                configuration = configuration with { ReciprocalPropertyId = reciprocalId };
            }
        }

        property.ConfigJson = editor.Type is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.MultiSelect
            or WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Relation or WikiDatabasePropertyTypes.Rollup
            or WikiDatabasePropertyTypes.Status or WikiDatabasePropertyTypes.Button or WikiDatabasePropertyTypes.UniqueId
            ? WikiDatabasePropertyConfig.Serialize(configuration)
            : "{}";

        await dbContext.SaveChangesAsync(cancellationToken);
        return property;
    }

    private async Task<Guid> EnsureReciprocalPropertyAsync(
        Guid sourceDatabaseId,
        WikiDatabaseProperty sourceProperty,
        WikiDatabasePropertyConfiguration sourceConfiguration,
        string? requestedName,
        DateTimeOffset now,
        string performedBy,
        CancellationToken cancellationToken)
    {
        var targetDatabaseId = sourceConfiguration.RelatedDatabaseId
            ?? throw new ArgumentException("A reciprocal relation requires a related database.");
        var reciprocal = sourceConfiguration.ReciprocalPropertyId is { } reciprocalId
            ? await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(item =>
                item.Id == reciprocalId && item.WikiDatabaseId == targetDatabaseId
                    && item.Type == WikiDatabasePropertyTypes.Relation, cancellationToken)
            : null;

        if (reciprocal is null)
        {
            var sourceDatabaseTitle = await dbContext.WikiDatabases
                .Where(item => item.Id == sourceDatabaseId)
                .Select(item => item.Title)
                .SingleAsync(cancellationToken);
            reciprocal = new WikiDatabaseProperty
            {
                WikiDatabaseId = targetDatabaseId,
                Name = string.IsNullOrWhiteSpace(requestedName) ? sourceDatabaseTitle : requestedName.Trim(),
                Type = WikiDatabasePropertyTypes.Relation,
                SortOrder = targetDatabaseId == sourceDatabaseId
                    ? sourceProperty.SortOrder + 1
                    : await NextPropertySortOrderAsync(targetDatabaseId, cancellationToken),
                CreatedAt = now,
                CreatedBy = performedBy
            };
            await dbContext.WikiDatabaseProperties.AddAsync(reciprocal, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(requestedName))
        {
            reciprocal.Name = requestedName.Trim();
            reciprocal.UpdatedAt = now;
            reciprocal.UpdatedBy = performedBy;
        }

        reciprocal.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            WikiDatabasePropertyConfiguration.Empty with
            {
                RelatedDatabaseId = sourceDatabaseId,
                ReciprocalPropertyId = sourceProperty.Id
            });
        return reciprocal.Id;
    }

    private async Task RemoveReciprocalPropertyAsync(
        Guid? reciprocalPropertyId,
        Guid sourcePropertyId,
        DateTimeOffset now,
        string performedBy,
        CancellationToken cancellationToken)
    {
        if (reciprocalPropertyId is not { } reciprocalId)
        {
            return;
        }

        var reciprocal = await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(
            item => item.Id == reciprocalId && item.Type == WikiDatabasePropertyTypes.Relation, cancellationToken);
        if (reciprocal is null
            || WikiDatabasePropertyConfig.Parse(reciprocal).ReciprocalPropertyId != sourcePropertyId)
        {
            return;
        }

        var rows = await dbContext.WikiDatabaseRows
            .Where(row => row.WikiDatabaseId == reciprocal.WikiDatabaseId)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            if (!values.Remove(reciprocal.Id.ToString())) continue;
            row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
            row.UpdatedAt = now;
            row.UpdatedBy = performedBy;
        }
        dbContext.WikiDatabaseProperties.Remove(reciprocal);
    }

    public async Task DeletePropertyAsync(Guid wikiDatabaseId, Guid propertyId, string performedBy, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseUnlockedAsync(wikiDatabaseId, cancellationToken);
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

        var now = DateTimeOffset.UtcNow;
        if (property.Type == WikiDatabasePropertyTypes.Relation)
        {
            await RemoveReciprocalPropertyAsync(
                WikiDatabasePropertyConfig.Parse(property).ReciprocalPropertyId,
                property.Id, now, performedBy, cancellationToken);
        }
        var rows = await dbContext.WikiDatabaseRows
            .Where(row => row.WikiDatabaseId == wikiDatabaseId)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            if (!values.Remove(property.Id.ToString())) continue;
            row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
            row.UpdatedAt = now;
            row.UpdatedBy = performedBy;
        }
        var rowTemplates = await dbContext.WikiDatabaseRowTemplates
            .Where(template => template.WikiDatabaseId == wikiDatabaseId)
            .ToListAsync(cancellationToken);
        foreach (var template in rowTemplates)
        {
            var defaults = WikiPropertyValues.ParseObject(template.DefaultPropertyValuesJson);
            if (!defaults.Remove(property.Id.ToString())) continue;
            template.DefaultPropertyValuesJson = WikiPropertyValues.Serialize(defaults);
            template.UpdatedAt = now;
            template.UpdatedBy = performedBy;
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
        if (row is { TrashedAt: not null })
        {
            // A stale editor tab (opened before the row was trashed elsewhere) shouldn't be
            // able to silently keep editing a trashed row - restore it first.
            throw new InvalidOperationException("This row has been moved to Trash. Restore it before saving changes.");
        }
        var previousValues = row is null
            ? new JsonObject()
            : WikiPropertyValues.ParseObject(row.PropertyValuesJson);

        var isNew = row is null;
        row ??= new WikiDatabaseRow
        {
            WikiDatabaseId = wikiDatabaseId,
            CreatedAt = now,
            CreatedBy = performedBy,
            SortOrder = await NextRowSortOrderAsync(wikiDatabaseId, cancellationToken)
        };

        await ValidateParentRowAsync(wikiDatabaseId, row.Id, editor.ParentRowId, cancellationToken);
        row.ParentRowId = editor.ParentRowId;

        var computedPropertyIds = await dbContext.WikiDatabaseProperties
            .Where(property => property.WikiDatabaseId == wikiDatabaseId
                && (property.Type == WikiDatabasePropertyTypes.Formula || property.Type == WikiDatabasePropertyTypes.Rollup
                    || property.Type == WikiDatabasePropertyTypes.LastEditedTime || property.Type == WikiDatabasePropertyTypes.LastEditedBy
                    || property.Type == WikiDatabasePropertyTypes.CreatedBy || property.Type == WikiDatabasePropertyTypes.Button
                    || property.Type == WikiDatabasePropertyTypes.UniqueId))
            .Select(property => property.Id.ToString())
            .ToListAsync(cancellationToken);
        var values = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in editor.Values)
        {
            if (!computedPropertyIds.Contains(key))
            {
                values[key] = value?.DeepClone();
            }
        }
        if (isNew)
        {
            // UniqueId is assigned exactly once, here, and never re-assigned on edit - the
            // number is the max already used by any sibling row (including trashed ones, so
            // numbers are never reused) plus one, mirroring Notion's auto-increment behavior.
            var uniqueIdProperties = await dbContext.WikiDatabaseProperties
                .Where(property => property.WikiDatabaseId == wikiDatabaseId && property.Type == WikiDatabasePropertyTypes.UniqueId)
                .ToListAsync(cancellationToken);
            if (uniqueIdProperties.Count > 0)
            {
                var siblingValuesJson = await dbContext.WikiDatabaseRows
                    .Where(sibling => sibling.WikiDatabaseId == wikiDatabaseId)
                    .Select(sibling => sibling.PropertyValuesJson)
                    .ToListAsync(cancellationToken);
                foreach (var uniqueIdProperty in uniqueIdProperties)
                {
                    var nextId = siblingValuesJson
                        .Select(json => WikiPropertyValues.GetNumber(WikiPropertyValues.ParseObject(json), uniqueIdProperty.Id))
                        .Where(number => number.HasValue)
                        .Select(number => number!.Value)
                        .DefaultIfEmpty(0m)
                        .Max() + 1;
                    WikiPropertyValues.SetNumber(values, uniqueIdProperty.Id, nextId);
                }
            }
        }
        else
        {
            // UniqueId is excluded from editor.Values above (it's never client-writable), but
            // editor.Values is the *complete* replacement set for this row - without carrying
            // the already-assigned number forward here, every subsequent edit of the row would
            // silently wipe it back to empty.
            foreach (var propertyId in computedPropertyIds)
            {
                if (previousValues.TryGetPropertyValue(propertyId, out var previousValue) && previousValue is not null)
                {
                    values[propertyId] = previousValue.DeepClone();
                }
            }
        }
        await ValidateTimelineDependenciesAsync(wikiDatabaseId, row.Id, values, cancellationToken);
        row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
        var propertyValuesChanged = isNew || !string.Equals(WikiPropertyValues.Serialize(previousValues), row.PropertyValuesJson, StringComparison.Ordinal);
        // Content-affecting fields, mirroring WikiPage's null-preserves convention: a
        // property-only save (e.g. AddInlineRowAsync's blank editor, or a board drag) leaves
        // the row's page body/icon/cover untouched rather than clearing them.
        var contentChanged = editor.BlocksJson is not null;
        if (contentChanged)
        {
            row.BlocksJson = string.IsNullOrWhiteSpace(editor.BlocksJson) ? "[]" : editor.BlocksJson;
        }
        if (editor.Icon is not null)
        {
            row.Icon = string.IsNullOrWhiteSpace(editor.Icon) ? null : editor.Icon.Trim();
        }
        if (editor.CoverImageUrl is not null)
        {
            row.CoverImageUrl = string.IsNullOrWhiteSpace(editor.CoverImageUrl) ? null : editor.CoverImageUrl.Trim();
        }
        row.UpdatedAt = now;
        row.UpdatedBy = performedBy;

        if (isNew)
        {
            await dbContext.WikiDatabaseRows.AddAsync(row, cancellationToken);
        }

        await SynchronizeReciprocalRelationsAsync(
            wikiDatabaseId, row.Id, previousValues, values, now, performedBy, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Snapshot history tracks the row's page body, not every property/cell edit - a
        // revision is only worth creating when the body itself was part of this save (i.e.
        // opened as a page and saved), same as why SaveInlineCellAsync never calls SaveRowAsync.
        // CreateRevisionCheckpoint additionally lets a silent background autosave persist
        // content without minting a new version on every debounced keystroke burst.
        if (contentChanged && editor.CreateRevisionCheckpoint)
        {
            await CreateRowRevisionAsync(row, performedBy, cancellationToken);
        }

        // Property-only edits (inline cell edits, board drags) and new rows both count as a
        // "row changed" event; a save that only touched the page body/icon/cover does not,
        // since nothing an automation could condition on (property values) actually moved.
        // The automation engine's own write-back node (database.setRowProperty) always saves
        // as this exact actor string specifically so this check can skip re-firing the trigger -
        // without it, a workflow whose write-back targets the database its own
        // database.rowChangedTrigger watches would re-trigger itself forever. This blocks all
        // automation chaining through this trigger, not just self-loops; a bounded, safe
        // default over building real recursion-depth tracking for a narrower guarantee.
        if (propertyValuesChanged && automationTriggerService is not null && performedBy != "automation-engine")
        {
            var triggerPayload = JsonSerializer.Serialize(new { wikiDatabaseId, rowId = row.Id, isNew, values });
            await automationTriggerService.TriggerDatabaseRowChangedAsync(wikiDatabaseId, triggerPayload, cancellationToken);
        }

        return row;
    }

    private async Task ValidateTimelineDependenciesAsync(
        Guid wikiDatabaseId,
        Guid changedRowId,
        JsonObject proposedValues,
        CancellationToken cancellationToken)
    {
        var timelineConfigJson = await dbContext.WikiDatabaseViews.AsNoTracking()
            .Where(view => view.WikiDatabaseId == wikiDatabaseId && view.Type == WikiDatabaseViewTypes.Timeline)
            .Select(view => view.ConfigJson)
            .ToListAsync(cancellationToken);
        var dependencyPropertyIds = timelineConfigJson
            .Select(configJson => WikiDatabaseViewConfigJson.Parse(configJson).DependencyPropertyId)
            .Where(value => Guid.TryParse(value, out _))
            .Select(value => Guid.Parse(value!))
            .Distinct()
            .ToList();
        if (dependencyPropertyIds.Count == 0)
        {
            return;
        }

        var dependencyProperties = await dbContext.WikiDatabaseProperties.AsNoTracking()
            .Where(property => property.WikiDatabaseId == wikiDatabaseId
                && dependencyPropertyIds.Contains(property.Id)
                && property.Type == WikiDatabasePropertyTypes.Relation)
            .ToListAsync(cancellationToken);
        var rows = await dbContext.WikiDatabaseRows.AsNoTracking()
            .Where(row => row.WikiDatabaseId == wikiDatabaseId)
            .Select(row => new { row.Id, row.PropertyValuesJson })
            .ToListAsync(cancellationToken);
        var validRowIds = rows.Select(row => row.Id).Append(changedRowId).ToHashSet();

        foreach (var dependencyProperty in dependencyProperties)
        {
            if (WikiDatabasePropertyConfig.Parse(dependencyProperty).RelatedDatabaseId != wikiDatabaseId)
            {
                continue;
            }

            var proposedDependencies = WikiPropertyValues.GetMultiSelect(proposedValues, dependencyProperty.Id)
                .Select(value => Guid.TryParse(value, out var rowId) ? new Guid?(rowId) : null)
                .Where(rowId => rowId.HasValue)
                .Select(rowId => rowId!.Value)
                .Distinct()
                .ToList();
            if (proposedDependencies.Any(dependencyRowId => !validRowIds.Contains(dependencyRowId)))
            {
                throw new InvalidOperationException("Timeline dependencies must reference rows in the same database.");
            }

            IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> graph = rows.ToDictionary(
                row => row.Id,
                row => (IReadOnlyList<Guid>)WikiPropertyValues.GetMultiSelect(
                        WikiPropertyValues.ParseObject(row.PropertyValuesJson), dependencyProperty.Id)
                    .Select(value => Guid.TryParse(value, out var dependencyRowId) ? new Guid?(dependencyRowId) : null)
                    .Where(dependencyRowId => dependencyRowId.HasValue)
                    .Select(dependencyRowId => dependencyRowId!.Value)
                    .Distinct()
                    .ToList());
            var mutableGraph = graph.ToDictionary(item => item.Key, item => item.Value);
            mutableGraph[changedRowId] = proposedDependencies;
            WikiDatabaseViewLogic.EnsureAcyclicTimelineDependencies(changedRowId, mutableGraph);
        }
    }

    private async Task ValidateParentRowAsync(
        Guid wikiDatabaseId,
        Guid rowId,
        Guid? proposedParentRowId,
        CancellationToken cancellationToken)
    {
        if (proposedParentRowId is null)
        {
            return;
        }
        if (proposedParentRowId == rowId)
        {
            throw new InvalidOperationException("A database row cannot be its own parent.");
        }

        var rowParents = await dbContext.WikiDatabaseRows
            .AsNoTracking()
            .Where(candidate => candidate.WikiDatabaseId == wikiDatabaseId)
            .Select(candidate => new { candidate.Id, candidate.ParentRowId })
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.ParentRowId, cancellationToken);
        if (!rowParents.ContainsKey(proposedParentRowId.Value))
        {
            throw new InvalidOperationException("A sub-item parent must belong to the same database.");
        }

        // Walk upward from the proposed parent. Reaching this row would create a cycle; a
        // repeated ancestor also rejects already-corrupt cyclic input instead of looping.
        var visited = new HashSet<Guid> { rowId };
        Guid? currentRowId = proposedParentRowId;
        while (currentRowId is { } current)
        {
            if (!visited.Add(current))
            {
                throw new InvalidOperationException("A database row cannot be nested beneath one of its own descendants.");
            }
            if (!rowParents.TryGetValue(current, out currentRowId))
            {
                throw new InvalidOperationException("Every sub-item ancestor must belong to the same database.");
            }
        }
    }

    public async Task<WikiDatabaseCsvImportResult> ImportCsvAsync(
        Guid wikiDatabaseId,
        string csv,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);

        // Parse and validate the complete document before adding properties or rows. A bad
        // quote/header therefore cannot leave a half-created schema behind, while row-local
        // shape/value problems can be reported and safely skipped below.
        var records = ParseBoundedCsv(csv);
        if (records.Count == 0)
        {
            throw new ArgumentException("CSV must contain a header row.", nameof(csv));
        }

        var headers = records[0]
            .Select((header, index) => (index == 0 ? header.TrimStart('\uFEFF') : header).Trim())
            .ToList();
        for (var index = 0; index < headers.Count; index++)
        {
            if (headers[index].Length == 0)
            {
                throw new ArgumentException($"CSV header column {index + 1} is blank.", nameof(csv));
            }
            if (headers[index].Length > MaxCsvHeaderCharacters)
            {
                throw new ArgumentException(
                    $"CSV header column {index + 1} exceeds the {MaxCsvHeaderCharacters}-character limit.",
                    nameof(csv));
            }
        }
        var duplicateHeader = headers
            .GroupBy(header => header, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateHeader is not null)
        {
            throw new ArgumentException($"CSV contains duplicate header '{duplicateHeader.Key}'.", nameof(csv));
        }

        if (!await dbContext.WikiDatabases.AsNoTracking().AnyAsync(
                database => database.Id == wikiDatabaseId && database.TrashedAt == null,
                cancellationToken))
        {
            throw new KeyNotFoundException("The database no longer exists.");
        }

        var existingProperties = await dbContext.WikiDatabaseProperties.AsNoTracking()
            .Where(property => property.WikiDatabaseId == wikiDatabaseId)
            .OrderBy(property => property.SortOrder)
            .ToListAsync(cancellationToken);
        var titleProperties = existingProperties
            .Where(property => property.Type == WikiDatabasePropertyTypes.Title)
            .ToList();
        if (titleProperties.Count != 1)
        {
            throw new InvalidOperationException("The target database must have exactly one Title property before importing CSV.");
        }

        var warnings = new List<string>();
        var suppressedWarnings = 0;
        var mappings = new WikiDatabaseProperty?[headers.Count];
        mappings[0] = titleProperties[0];
        var propertiesToCreate = new List<(int ColumnIndex, string Name)>();
        var propertiesByName = existingProperties
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < headers.Count; index++)
        {
            if (!propertiesByName.TryGetValue(headers[index], out var matchingProperties))
            {
                propertiesToCreate.Add((index, headers[index]));
                continue;
            }
            if (matchingProperties.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The target database contains multiple properties named '{headers[index]}', so that CSV column is ambiguous.");
            }

            var property = matchingProperties[0];
            if (property.Id == titleProperties[0].Id)
            {
                throw new ArgumentException(
                    $"CSV column '{headers[index]}' maps to the Title property already assigned to the first column.",
                    nameof(csv));
            }
            if (IsCsvSystemManagedProperty(property.Type))
            {
                AddCsvWarning(
                    warnings,
                    ref suppressedWarnings,
                    $"CSV column '{headers[index]}' maps to read-only property '{property.Name}' and was ignored.");
                continue;
            }
            if (property.Type == WikiDatabasePropertyTypes.Relation)
            {
                AddCsvWarning(
                    warnings,
                    ref suppressedWarnings,
                    $"CSV column '{headers[index]}' maps to Relation property '{property.Name}', whose row links cannot be resolved safely from standalone CSV, and was ignored.");
                continue;
            }

            mappings[index] = property;
        }

        var rowsToImport = new List<(int SourceRowNumber, List<string> Fields)>();
        var rowsSkipped = 0;
        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var sourceRowNumber = recordIndex + 1;
            var fields = records[recordIndex];
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                rowsSkipped++;
                AddCsvWarning(warnings, ref suppressedWarnings, $"Row {sourceRowNumber} is blank and was skipped.");
                continue;
            }
            if (fields.Count != headers.Count)
            {
                rowsSkipped++;
                AddCsvWarning(
                    warnings,
                    ref suppressedWarnings,
                    $"Row {sourceRowNumber} has {fields.Count} column(s); expected {headers.Count}. The row was skipped.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(fields[0]))
            {
                rowsSkipped++;
                AddCsvWarning(warnings, ref suppressedWarnings, $"Row {sourceRowNumber} has no title and was skipped.");
                continue;
            }
            rowsToImport.Add((sourceRowNumber, fields));
        }

        // A header-only file (or one whose every data record was rejected) must not mutate
        // the target schema merely because it named columns that do not exist yet.
        if (rowsToImport.Count == 0)
        {
            if (suppressedWarnings > 0)
            {
                warnings.Add($"{suppressedWarnings:N0} additional CSV import warning(s) were omitted.");
            }
            return new WikiDatabaseCsvImportResult(0, rowsSkipped, 0, warnings);
        }

        foreach (var (columnIndex, name) in propertiesToCreate)
        {
            mappings[columnIndex] = await SavePropertyAsync(
                wikiDatabaseId,
                new WikiDatabasePropertyEditor { Name = name, Type = WikiDatabasePropertyTypes.Text },
                performedBy,
                cancellationToken);
        }

        var rowsImported = 0;
        foreach (var (sourceRowNumber, fields) in rowsToImport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new JsonObject();
            for (var columnIndex = 0; columnIndex < mappings.Length; columnIndex++)
            {
                var property = mappings[columnIndex];
                if (property is null)
                {
                    continue;
                }
                SetCsvImportValue(
                    values,
                    property,
                    fields[columnIndex],
                    sourceRowNumber,
                    warnings,
                    ref suppressedWarnings);
            }

            await SaveRowAsync(
                wikiDatabaseId,
                new WikiDatabaseRowEditor
                {
                    ParentRowId = null,
                    Values = values.ToDictionary(item => item.Key, item => item.Value)
                },
                performedBy,
                cancellationToken);
            rowsImported++;
        }

        if (suppressedWarnings > 0)
        {
            warnings.Add($"{suppressedWarnings:N0} additional CSV import warning(s) were omitted.");
        }
        return new WikiDatabaseCsvImportResult(
            rowsImported,
            rowsSkipped,
            propertiesToCreate.Count,
            warnings);
    }

    private static bool IsCsvSystemManagedProperty(string propertyType) => propertyType is
        WikiDatabasePropertyTypes.Formula
        or WikiDatabasePropertyTypes.Rollup
        or WikiDatabasePropertyTypes.CreatedTime
        or WikiDatabasePropertyTypes.CreatedBy
        or WikiDatabasePropertyTypes.LastEditedTime
        or WikiDatabasePropertyTypes.LastEditedBy
        or WikiDatabasePropertyTypes.Button
        or WikiDatabasePropertyTypes.UniqueId
        or WikiDatabasePropertyTypes.Verification;

    private static void SetCsvImportValue(
        JsonObject values,
        WikiDatabaseProperty property,
        string rawValue,
        int sourceRowNumber,
        List<string> warnings,
        ref int suppressedWarnings)
    {
        if (property.Type == WikiDatabasePropertyTypes.Title)
        {
            WikiPropertyValues.SetText(values, property.Id, rawValue.Trim());
            return;
        }
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var value = rawValue.Trim();
        switch (property.Type)
        {
            case WikiDatabasePropertyTypes.Number:
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    WikiPropertyValues.SetNumber(values, property.Id, number);
                }
                else
                {
                    AddCsvWarning(
                        warnings,
                        ref suppressedWarnings,
                        $"Row {sourceRowNumber}: '{CsvWarningValue(value)}' is not a valid number for '{property.Name}'; the value was left empty.");
                }
                break;
            case WikiDatabasePropertyTypes.Checkbox:
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                    || value == "1")
                {
                    WikiPropertyValues.SetCheckbox(values, property.Id, true);
                }
                else if (value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("n", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || value == "0")
                {
                    WikiPropertyValues.SetCheckbox(values, property.Id, false);
                }
                else
                {
                    AddCsvWarning(
                        warnings,
                        ref suppressedWarnings,
                        $"Row {sourceRowNumber}: '{CsvWarningValue(value)}' is not a valid checkbox value for '{property.Name}'; the value was left unchecked.");
                }
                break;
            case WikiDatabasePropertyTypes.Date:
                if (DateTimeOffset.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                        out var date))
                {
                    WikiPropertyValues.SetDate(values, property.Id, date);
                }
                else
                {
                    AddCsvWarning(
                        warnings,
                        ref suppressedWarnings,
                        $"Row {sourceRowNumber}: '{CsvWarningValue(value)}' is not a valid date for '{property.Name}'; the value was left empty.");
                }
                break;
            case WikiDatabasePropertyTypes.Select:
            case WikiDatabasePropertyTypes.Status:
                var optionId = ResolveCsvOptionId(property, value);
                if (optionId is null)
                {
                    AddCsvWarning(
                        warnings,
                        ref suppressedWarnings,
                        $"Row {sourceRowNumber}: '{CsvWarningValue(value)}' is not a configured option for '{property.Name}'; the value was left empty.");
                }
                else
                {
                    WikiPropertyValues.SetText(values, property.Id, optionId);
                }
                break;
            case WikiDatabasePropertyTypes.MultiSelect:
                var selectedOptionIds = new List<string>();
                var invalidOptions = new List<string>();
                foreach (var candidate in SplitCsvCollection(value))
                {
                    var selectedOptionId = ResolveCsvOptionId(property, candidate);
                    if (selectedOptionId is null)
                    {
                        invalidOptions.Add(candidate);
                    }
                    else if (!selectedOptionIds.Contains(selectedOptionId, StringComparer.Ordinal))
                    {
                        selectedOptionIds.Add(selectedOptionId);
                    }
                }
                if (invalidOptions.Count > 0)
                {
                    AddCsvWarning(
                        warnings,
                        ref suppressedWarnings,
                        $"Row {sourceRowNumber}: {invalidOptions.Count} unconfigured option(s) for '{property.Name}' were ignored ({string.Join(", ", invalidOptions.Take(3).Select(CsvWarningValue))}).");
                }
                WikiPropertyValues.SetMultiSelect(values, property.Id, selectedOptionIds);
                break;
            case WikiDatabasePropertyTypes.Person:
            case WikiDatabasePropertyTypes.Files:
                WikiPropertyValues.SetMultiSelect(values, property.Id, SplitCsvCollection(value));
                break;
            case WikiDatabasePropertyTypes.Text:
            case WikiDatabasePropertyTypes.Url:
            case WikiDatabasePropertyTypes.Email:
            case WikiDatabasePropertyTypes.Phone:
            case WikiDatabasePropertyTypes.Place:
                WikiPropertyValues.SetText(values, property.Id, rawValue);
                break;
            default:
                AddCsvWarning(
                    warnings,
                    ref suppressedWarnings,
                    $"Row {sourceRowNumber}: '{property.Name}' uses an unsupported property type and was ignored.");
                break;
        }
    }

    private static string? ResolveCsvOptionId(WikiDatabaseProperty property, string candidate)
    {
        var options = WikiDatabasePropertyConfig.GetOptions(property);
        var exactIdMatches = options
            .Where(item => string.Equals(item.Id, candidate, StringComparison.Ordinal))
            .ToList();
        if (exactIdMatches.Count == 1)
        {
            return exactIdMatches[0].Id;
        }
        var labelMatches = options
            .Where(item => string.Equals(item.Label, candidate, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return labelMatches.Count == 1 ? labelMatches[0].Id : null;
    }

    private static IReadOnlyList<string> SplitCsvCollection(string value) => value
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string CsvWarningValue(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 80 ? singleLine : $"{singleLine[..77]}...";
    }

    private static void AddCsvWarning(List<string> warnings, ref int suppressedWarnings, string warning)
    {
        if (warnings.Count < MaxCsvWarnings)
        {
            warnings.Add(warning);
        }
        else
        {
            suppressedWarnings++;
        }
    }

    private static List<List<string>> ParseBoundedCsv(string csv)
    {
        if (Encoding.UTF8.GetByteCount(csv) > WikiDatabaseCsvImportLimits.MaxFileBytes)
        {
            throw new ArgumentException(
                $"CSV exceeds the {WikiDatabaseCsvImportLimits.MaxFileBytes / 1024 / 1024} MB import limit.",
                nameof(csv));
        }

        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var quoteClosed = false;
        var recordStarted = false;

        void AppendCharacter(char character)
        {
            if (field.Length >= MaxCsvFieldCharacters)
            {
                throw new ArgumentException(
                    $"CSV row {records.Count + 1} contains a field longer than {MaxCsvFieldCharacters:N0} characters.",
                    nameof(csv));
            }
            field.Append(character);
            recordStarted = true;
        }

        void CompleteField()
        {
            record.Add(field.ToString());
            if (record.Count > WikiDatabaseCsvImportLimits.MaxColumns)
            {
                throw new ArgumentException(
                    $"CSV row {records.Count + 1} exceeds the {WikiDatabaseCsvImportLimits.MaxColumns}-column limit.",
                    nameof(csv));
            }
            field.Clear();
            quoteClosed = false;
            recordStarted = true;
        }

        void CompleteRecord()
        {
            CompleteField();
            records.Add(record);
            if (records.Count > WikiDatabaseCsvImportLimits.MaxRows + 1)
            {
                throw new ArgumentException(
                    $"CSV exceeds the {WikiDatabaseCsvImportLimits.MaxRows:N0}-row import limit.",
                    nameof(csv));
            }
            record = [];
            recordStarted = false;
        }

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '\0')
            {
                throw new ArgumentException(
                    $"CSV row {records.Count + 1} contains a null character.",
                    nameof(csv));
            }
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        AppendCharacter('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        quoteClosed = true;
                    }
                }
                else
                {
                    AppendCharacter(character);
                }
                continue;
            }

            if (quoteClosed)
            {
                if (character is ' ' or '\t')
                {
                    continue;
                }
                if (character == ',')
                {
                    CompleteField();
                    continue;
                }
                if (character is '\r' or '\n')
                {
                    if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                    {
                        index++;
                    }
                    CompleteRecord();
                    continue;
                }
                throw new ArgumentException(
                    $"CSV row {records.Count + 1} contains an unexpected character after a closing quote.",
                    nameof(csv));
            }

            if (character == '"')
            {
                if (field.Length != 0)
                {
                    throw new ArgumentException(
                        $"CSV row {records.Count + 1} contains an unexpected quote in an unquoted field.",
                        nameof(csv));
                }
                quoted = true;
                recordStarted = true;
            }
            else if (character == ',')
            {
                CompleteField();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }
                CompleteRecord();
            }
            else
            {
                AppendCharacter(character);
            }
        }

        if (quoted)
        {
            throw new ArgumentException($"CSV row {records.Count + 1} has an unterminated quoted field.", nameof(csv));
        }
        if (recordStarted || record.Count > 0 || field.Length > 0 || quoteClosed)
        {
            CompleteRecord();
        }
        return records;
    }

    public async Task<IReadOnlyList<WikiDatabaseRowTemplate>> ListRowTemplatesAsync(
        Guid wikiDatabaseId,
        CancellationToken cancellationToken = default) =>
        await dbContext.WikiDatabaseRowTemplates
            .AsNoTracking()
            .Where(template => template.WikiDatabaseId == wikiDatabaseId)
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);

    public async Task<WikiDatabaseRowTemplate> CreateRowTemplateFromRowAsync(
        Guid wikiDatabaseId,
        Guid sourceRowId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseUnlockedAsync(wikiDatabaseId, cancellationToken);
        var normalizedName = NormalizeRowTemplateName(name);
        if (await dbContext.WikiDatabaseRowTemplates.AnyAsync(
                template => template.WikiDatabaseId == wikiDatabaseId && template.NormalizedName == normalizedName,
                cancellationToken))
        {
            throw new InvalidOperationException("A row template with that name already exists in this database.");
        }

        var sourceRow = await dbContext.WikiDatabaseRows.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == sourceRowId && row.WikiDatabaseId == wikiDatabaseId && row.TrashedAt == null,
                cancellationToken)
            ?? throw new InvalidOperationException("Choose an active row from this database as the template source.");
        var reusablePropertyIds = (await dbContext.WikiDatabaseProperties.AsNoTracking()
                .Where(property => property.WikiDatabaseId == wikiDatabaseId)
                .Select(property => new { property.Id, property.Type })
                .ToListAsync(cancellationToken))
            .Where(property => IsReusableTemplateProperty(property.Type))
            .Select(property => property.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var sourceValues = WikiPropertyValues.ParseObject(sourceRow.PropertyValuesJson);
        var defaults = new JsonObject();
        foreach (var (propertyId, value) in sourceValues)
        {
            if (value is not null && reusablePropertyIds.Contains(propertyId))
            {
                defaults[propertyId] = value.DeepClone();
            }
        }

        var template = new WikiDatabaseRowTemplate
        {
            WikiDatabaseId = wikiDatabaseId,
            Name = name.Trim(),
            NormalizedName = normalizedName,
            BlocksJson = sourceRow.BlocksJson,
            DefaultPropertyValuesJson = WikiPropertyValues.Serialize(defaults),
            Icon = sourceRow.Icon,
            CoverImageUrl = sourceRow.CoverImageUrl,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = performedBy
        };
        await dbContext.WikiDatabaseRowTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return template;
    }

    public async Task<WikiDatabaseRow> CreateRowFromTemplateAsync(
        Guid wikiDatabaseId,
        Guid templateId,
        Guid? parentRowId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.WikiDatabaseRowTemplates.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == templateId && item.WikiDatabaseId == wikiDatabaseId,
                cancellationToken)
            ?? throw new InvalidOperationException("The selected row template no longer exists in this database.");
        var reusablePropertyIds = (await dbContext.WikiDatabaseProperties.AsNoTracking()
                .Where(property => property.WikiDatabaseId == wikiDatabaseId)
                .Select(property => new { property.Id, property.Type })
                .ToListAsync(cancellationToken))
            .Where(property => IsReusableTemplateProperty(property.Type))
            .Select(property => property.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var storedDefaults = WikiPropertyValues.ParseObject(template.DefaultPropertyValuesJson);
        var values = new JsonObject();
        foreach (var (propertyId, value) in storedDefaults)
        {
            if (value is not null && reusablePropertyIds.Contains(propertyId))
            {
                values[propertyId] = value.DeepClone();
            }
        }
        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson)
            .Select(block => block with { Id = Guid.NewGuid() })
            .ToList();

        return await SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor
        {
            ParentRowId = parentRowId,
            BlocksJson = WikiBlockJson.Serialize(blocks),
            Icon = template.Icon ?? string.Empty,
            CoverImageUrl = template.CoverImageUrl ?? string.Empty,
            Values = values.ToDictionary(item => item.Key, item => item.Value)
        }, performedBy, cancellationToken);
    }

    public async Task DeleteRowTemplateAsync(
        Guid wikiDatabaseId,
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseUnlockedAsync(wikiDatabaseId, cancellationToken);
        var template = await dbContext.WikiDatabaseRowTemplates.FirstOrDefaultAsync(
            item => item.Id == templateId && item.WikiDatabaseId == wikiDatabaseId,
            cancellationToken);
        if (template is null)
        {
            return;
        }
        dbContext.WikiDatabaseRowTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsReusableTemplateProperty(string propertyType) => propertyType is not (
        WikiDatabasePropertyTypes.Title
        or WikiDatabasePropertyTypes.Formula
        or WikiDatabasePropertyTypes.Rollup
        or WikiDatabasePropertyTypes.CreatedTime
        or WikiDatabasePropertyTypes.CreatedBy
        or WikiDatabasePropertyTypes.LastEditedTime
        or WikiDatabasePropertyTypes.LastEditedBy
        or WikiDatabasePropertyTypes.Button
        or WikiDatabasePropertyTypes.UniqueId
        or WikiDatabasePropertyTypes.Verification);

    private static string NormalizeRowTemplateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A row template name is required.", nameof(name));
        }
        if (trimmed.Length > 120)
        {
            throw new ArgumentException("Row template names cannot exceed 120 characters.", nameof(name));
        }
        return trimmed.ToUpperInvariant();
    }

    private async Task SynchronizeReciprocalRelationsAsync(
        Guid sourceDatabaseId,
        Guid sourceRowId,
        JsonObject previousValues,
        JsonObject currentValues,
        DateTimeOffset now,
        string performedBy,
        CancellationToken cancellationToken)
    {
        var relationProperties = await dbContext.WikiDatabaseProperties.AsNoTracking()
            .Where(property => property.WikiDatabaseId == sourceDatabaseId
                && property.Type == WikiDatabasePropertyTypes.Relation)
            .ToListAsync(cancellationToken);

        foreach (var relationProperty in relationProperties)
        {
            var config = WikiDatabasePropertyConfig.Parse(relationProperty);
            if (config.RelatedDatabaseId is not { } targetDatabaseId
                || config.ReciprocalPropertyId is not { } reciprocalPropertyId)
            {
                continue;
            }

            var reciprocalProperty = await dbContext.WikiDatabaseProperties.AsNoTracking().FirstOrDefaultAsync(property =>
                property.Id == reciprocalPropertyId
                    && property.WikiDatabaseId == targetDatabaseId
                    && property.Type == WikiDatabasePropertyTypes.Relation,
                cancellationToken);
            if (reciprocalProperty is null
                || WikiDatabasePropertyConfig.Parse(reciprocalProperty).ReciprocalPropertyId != relationProperty.Id)
            {
                continue;
            }

            var previousIds = WikiPropertyValues.GetMultiSelect(previousValues, relationProperty.Id)
                .Where(value => Guid.TryParse(value, out _))
                .ToHashSet(StringComparer.Ordinal);
            var currentIds = WikiPropertyValues.GetMultiSelect(currentValues, relationProperty.Id)
                .Where(value => Guid.TryParse(value, out _))
                .ToHashSet(StringComparer.Ordinal);
            var affectedIds = previousIds.Union(currentIds)
                .Select(Guid.Parse)
                .ToList();
            if (affectedIds.Count == 0)
            {
                continue;
            }

            var targetRows = await dbContext.WikiDatabaseRows
                .Where(row => row.WikiDatabaseId == targetDatabaseId && affectedIds.Contains(row.Id))
                .ToListAsync(cancellationToken);
            foreach (var targetRow in targetRows)
            {
                var targetValues = WikiPropertyValues.ParseObject(targetRow.PropertyValuesJson);
                var reverseIds = WikiPropertyValues.GetMultiSelect(targetValues, reciprocalPropertyId)
                    .ToHashSet(StringComparer.Ordinal);
                if (currentIds.Contains(targetRow.Id.ToString()))
                {
                    reverseIds.Add(sourceRowId.ToString());
                }
                else
                {
                    reverseIds.Remove(sourceRowId.ToString());
                }
                WikiPropertyValues.SetMultiSelect(targetValues, reciprocalPropertyId, reverseIds.ToList());
                targetRow.PropertyValuesJson = WikiPropertyValues.Serialize(targetValues);
                targetRow.UpdatedAt = now;
                targetRow.UpdatedBy = performedBy;
            }
        }
    }

    private async Task ValidatePropertyConfigurationAsync(
        Guid wikiDatabaseId,
        Guid propertyId,
        string propertyType,
        WikiDatabasePropertyConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (propertyType == WikiDatabasePropertyTypes.Formula)
        {
            var availablePropertyNames = await dbContext.WikiDatabaseProperties.AsNoTracking()
                .Where(property => property.WikiDatabaseId == wikiDatabaseId && property.Id != propertyId)
                .Select(property => property.Name)
                .ToListAsync(cancellationToken);
            WikiDatabaseComputation.ValidateFormula(configuration.FormulaExpression ?? string.Empty, availablePropertyNames);
            return;
        }

        if (propertyType == WikiDatabasePropertyTypes.Relation)
        {
            if (configuration.RelatedDatabaseId is not { } relatedDatabaseId
                || !await dbContext.WikiDatabases.AnyAsync(item => item.Id == relatedDatabaseId, cancellationToken))
            {
                throw new ArgumentException("A relation must target an existing Sentinel database.");
            }
            return;
        }

        // A Button with no workflow bound yet is allowed (an admin creating the property
        // before finishing Automation setup elsewhere) - only reject a workflow id that
        // doesn't actually exist.
        if (propertyType == WikiDatabasePropertyTypes.Button)
        {
            if (configuration.AutomationWorkflowId is { } workflowId
                && !await dbContext.AutomationWorkflows.AnyAsync(item => item.Id == workflowId, cancellationToken))
            {
                throw new ArgumentException("The selected automation workflow does not exist.");
            }
            return;
        }

        if (propertyType != WikiDatabasePropertyTypes.Rollup)
        {
            return;
        }

        if (configuration.RelationPropertyId is not { } relationPropertyId)
        {
            throw new ArgumentException("A rollup requires a relation property.");
        }
        var relationProperty = await dbContext.WikiDatabaseProperties.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == relationPropertyId
                && item.WikiDatabaseId == wikiDatabaseId
                && item.Type == WikiDatabasePropertyTypes.Relation, cancellationToken)
            ?? throw new ArgumentException("The selected relation property does not exist in this database.");
        var relationConfig = WikiDatabasePropertyConfig.Parse(relationProperty);
        if (relationConfig.RelatedDatabaseId is not { } rollupRelatedDatabaseId
            || configuration.RollupPropertyId is not { } rollupPropertyId
            || !await dbContext.WikiDatabaseProperties.AnyAsync(item =>
                item.WikiDatabaseId == rollupRelatedDatabaseId && item.Id == rollupPropertyId, cancellationToken))
        {
            throw new ArgumentException("The selected rollup property does not exist in the related database.");
        }
        if (!WikiDatabaseRollupAggregations.All.Contains(configuration.RollupAggregation ?? string.Empty))
        {
            throw new ArgumentException("Choose a supported rollup calculation.");
        }
        if (relationProperty.Id == propertyId)
        {
            throw new ArgumentException("A rollup cannot use itself as its relation.");
        }
    }

    public async Task<IReadOnlyList<WikiDatabaseRow>> ListTrashedRowsAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WikiDatabaseRows
            .AsNoTracking()
            .Where(row => row.WikiDatabaseId == wikiDatabaseId && row.TrashedAt != null)
            .ToListAsync(cancellationToken);

        // SQLite can't translate ORDER BY on a DateTimeOffset column - order client-side
        // after materializing (same pattern used throughout this app).
        return rows.OrderByDescending(row => row.TrashedAt).ToList();
    }

    public async Task TrashRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (row is null || row.TrashedAt is not null)
        {
            return;
        }

        // Deliberately leaves other rows' Relation references and child ParentRowId values
        // alone - trash is reversible. Children remain active and normal views render them as
        // roots while their parent is hidden; restoring the parent restores the hierarchy.
        row.TrashedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = row.TrashedAt;
        row.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (row is null || row.TrashedAt is null)
        {
            return;
        }

        row.TrashedAt = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRowPermanentlyAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken);
        if (row is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var inboundRelations = (await dbContext.WikiDatabaseProperties.AsNoTracking()
                .Where(property => property.Type == WikiDatabasePropertyTypes.Relation)
                .ToListAsync(cancellationToken))
            .Where(property => WikiDatabasePropertyConfig.Parse(property).RelatedDatabaseId == wikiDatabaseId)
            .ToList();
        foreach (var relation in inboundRelations)
        {
            var sourceRows = await dbContext.WikiDatabaseRows
                .Where(item => item.WikiDatabaseId == relation.WikiDatabaseId)
                .ToListAsync(cancellationToken);
            foreach (var sourceRow in sourceRows)
            {
                var values = WikiPropertyValues.ParseObject(sourceRow.PropertyValuesJson);
                var selected = WikiPropertyValues.GetMultiSelect(values, relation.Id).ToList();
                if (!selected.Remove(rowId.ToString())) continue;
                WikiPropertyValues.SetMultiSelect(values, relation.Id, selected);
                sourceRow.PropertyValuesJson = WikiPropertyValues.Serialize(values);
                sourceRow.UpdatedAt = now;
                sourceRow.UpdatedBy = performedBy;
            }
        }
        dbContext.WikiDatabaseRows.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WikiInlineDatabaseSnapshot?> GetInlineDatabaseAsync(
        Guid wikiDatabaseId,
        CancellationToken cancellationToken = default)
    {
        var database = await GetDatabaseAsync(wikiDatabaseId, cancellationToken);
        return database is null ? null : BuildInlineSnapshot(database);
    }

    public async Task<WikiInlineDatabaseSnapshot?> GetLinkedDatabaseAsync(
        Guid wikiDatabaseId,
        Guid? viewId,
        CancellationToken cancellationToken = default)
    {
        var database = await GetDatabaseAsync(wikiDatabaseId, cancellationToken);
        if (database is null)
        {
            return null;
        }

        var view = viewId is { } selectedViewId
            ? database.Views.FirstOrDefault(candidate => candidate.Id == selectedViewId)
            : database.Views.OrderBy(candidate => candidate.SortOrder).FirstOrDefault();
        if (view is null)
        {
            return null;
        }

        var config = WikiDatabaseViewConfigJson.Parse(view.ConfigJson);
        var rows = WikiDatabaseViewLogic.ApplyFilters(
            database.Rows.ToList(), database.Properties.ToList(), config.Filters, config.FilterGroup);
        rows = WikiDatabaseViewLogic.ApplySort(rows, database.Properties.ToList(), config.Sorts);
        return BuildInlineSnapshot(database, rows, [view]);
    }

    public async Task<WikiInlineDatabaseSnapshot> AddInlineRowAsync(
        Guid wikiDatabaseId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        await SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor(), performedBy, cancellationToken);
        return await GetInlineDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
    }

    public async Task<WikiInlineDatabaseSnapshot> AddInlineBoardRowAsync(
        Guid wikiDatabaseId,
        Guid groupByPropertyId,
        string? groupOptionId,
        string? title,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var database = await GetDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
        var groupProperty = database.Properties.FirstOrDefault(property =>
            property.Id == groupByPropertyId
            && property.Type is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status)
            ?? throw new InvalidOperationException("The board grouping property is no longer available.");
        var normalizedOptionId = string.IsNullOrWhiteSpace(groupOptionId) ? null : groupOptionId;
        if (normalizedOptionId is not null
            && !WikiDatabasePropertyConfig.GetOptions(groupProperty).Any(option => option.Id == normalizedOptionId))
        {
            throw new InvalidOperationException("The board column is no longer available.");
        }

        var values = new JsonObject();
        WikiPropertyValues.SetText(values, groupByPropertyId, normalizedOptionId);
        var titleProperty = database.Properties.FirstOrDefault(
            property => property.Type == WikiDatabasePropertyTypes.Title);
        if (titleProperty is not null && !string.IsNullOrWhiteSpace(title))
        {
            WikiPropertyValues.SetText(values, titleProperty.Id, title.Trim());
        }
        await SaveRowAsync(
            wikiDatabaseId,
            new WikiDatabaseRowEditor
            {
                Values = values.ToDictionary(item => item.Key, item => item.Value)
            },
            performedBy,
            cancellationToken);
        return await GetInlineDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
    }

    public async Task<WikiInlineDatabaseSnapshot> SaveInlineCellAsync(
        Guid wikiDatabaseId,
        Guid rowId,
        Guid propertyId,
        string? value,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var database = await GetDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
        var property = database.Properties.FirstOrDefault(item => item.Id == propertyId)
            ?? throw new KeyNotFoundException("The database property no longer exists.");
        var row = database.Rows.FirstOrDefault(item => item.Id == rowId)
            ?? throw new KeyNotFoundException("The database row no longer exists.");
        if (property.Type is WikiDatabasePropertyTypes.CreatedTime or WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup
            or WikiDatabasePropertyTypes.LastEditedTime or WikiDatabasePropertyTypes.LastEditedBy or WikiDatabasePropertyTypes.CreatedBy
            or WikiDatabasePropertyTypes.Button or WikiDatabasePropertyTypes.UniqueId)
        {
            throw new InvalidOperationException("Computed properties are read-only.");
        }
        // This method's `value` parameter is always a single string - fine for the scalar
        // types the switch below handles, but Relation/Person/Files are JSON-array-shaped
        // (see WikiPropertyValues.SetRelation/SetPerson/SetFiles) and every reader (dependent
        // rollups, reciprocal relation sync, the row detail panel) expects an array, not a
        // scalar. Silently falling through to the switch's `default: SetText(...)` branch used
        // to save a plain string here, which those readers then silently read back as empty -
        // no error, no visible sign the edit did nothing real. The inline embed's cell editor
        // (wiki-block-editor.js) no longer offers an editable control for these three types for
        // the same reason; this is the server-side half of that fix, in case anything else ever
        // calls this method directly.
        if (property.Type is WikiDatabasePropertyTypes.Relation or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Files)
        {
            throw new InvalidOperationException(
                $"{property.Type} properties can't be edited from this inline view yet - open the row to edit it.");
        }

        var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
        switch (property.Type)
        {
            case WikiDatabasePropertyTypes.Number:
                WikiPropertyValues.SetNumber(values, property.Id,
                    decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null);
                break;
            case WikiDatabasePropertyTypes.Checkbox:
                WikiPropertyValues.SetCheckbox(values, property.Id, bool.TryParse(value, out var isChecked) && isChecked);
                break;
            case WikiDatabasePropertyTypes.Date:
                WikiPropertyValues.SetDate(values, property.Id,
                    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null);
                break;
            case WikiDatabasePropertyTypes.MultiSelect:
                var validOptionIds = WikiDatabasePropertyConfig.GetOptions(property).Select(option => option.Id).ToHashSet();
                var selectedIds = (value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(validOptionIds.Contains)
                    .Distinct()
                    .ToList();
                WikiPropertyValues.SetMultiSelect(values, property.Id, selectedIds);
                break;
            case WikiDatabasePropertyTypes.Select:
            case WikiDatabasePropertyTypes.Status:
                var selectedId = WikiDatabasePropertyConfig.GetOptions(property)
                    .Any(option => option.Id == value) ? value : null;
                WikiPropertyValues.SetText(values, property.Id, selectedId);
                break;
            case WikiDatabasePropertyTypes.Verification:
                WikiPropertyValues.SetVerification(values, property.Id, value == WikiVerificationState.Verified
                    ? new WikiVerificationState(WikiVerificationState.Verified, performedBy, DateTimeOffset.UtcNow)
                    : WikiVerificationState.NotVerified);
                break;
            default:
                WikiPropertyValues.SetText(values, property.Id, value);
                break;
        }

        await SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor
        {
            Id = rowId,
            ParentRowId = row.ParentRowId,
            Values = values.ToDictionary(item => item.Key, item => item.Value)
        }, performedBy, cancellationToken);

        return await GetInlineDatabaseAsync(wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
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
        // Unlike SaveInlineCellAsync's sibling validation, this used to trust groupByPropertyId
        // and newGroupOptionId outright - a malformed client call could overwrite an arbitrary
        // property (including Title or a Relation, neither of which is a valid Kanban column)
        // with a raw string, or set an option id that doesn't exist on the property at all.
        // Board views are only ever grouped by a Select or Status property (see
        // AddBoardViewAsync), so those are the only types this accepts.
        var groupByProperty = await dbContext.WikiDatabaseProperties.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == groupByPropertyId && item.WikiDatabaseId == wikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The group-by property no longer exists.");
        if (groupByProperty.Type is not (WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.Status))
        {
            throw new InvalidOperationException("Rows can only be grouped and moved by a Select or Status property.");
        }
        if (newGroupOptionId is not null
            && !WikiDatabasePropertyConfig.GetOptions(groupByProperty).Any(option => option.Id == newGroupOptionId))
        {
            throw new InvalidOperationException("The target group option no longer exists.");
        }

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
        await EnsureDatabaseUnlockedAsync(wikiDatabaseId, cancellationToken);
        if (type == WikiDatabaseViewTypes.Timeline)
        {
            if (!Guid.TryParse(config.GroupByPropertyId, out var datePropertyId)
                || !await dbContext.WikiDatabaseProperties.AsNoTracking().AnyAsync(property =>
                    property.Id == datePropertyId
                    && property.WikiDatabaseId == wikiDatabaseId
                    && property.Type == WikiDatabasePropertyTypes.Date,
                    cancellationToken))
            {
                throw new InvalidOperationException("A Timeline view requires a Date property from this database.");
            }
            if (config.DependencyPropertyId is { Length: > 0 } dependencyPropertyValue
                && (!Guid.TryParse(dependencyPropertyValue, out var dependencyPropertyId)
                    || !await dbContext.WikiDatabaseProperties.AsNoTracking().AnyAsync(property =>
                        property.Id == dependencyPropertyId
                        && property.WikiDatabaseId == wikiDatabaseId
                        && property.Type == WikiDatabasePropertyTypes.Relation,
                        cancellationToken)))
            {
                throw new InvalidOperationException("Timeline dependencies require a Relation property from this database.");
            }
        }
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
        await EnsureDatabaseUnlockedAsync(wikiDatabaseId, cancellationToken);
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

    public async Task<WikiDatabaseViewConfig?> GetPersonalViewOverrideAsync(Guid viewId, string username, CancellationToken cancellationToken = default)
    {
        var personalization = await dbContext.WikiDatabaseViewPersonalizations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WikiDatabaseViewId == viewId && item.Username == username, cancellationToken);
        return personalization is null ? null : WikiDatabaseViewConfigJson.Parse(personalization.ConfigJson);
    }

    public async Task<WikiDatabaseViewConfig> SavePersonalViewOverrideAsync(
        Guid viewId, WikiDatabaseViewConfig overrideConfig, string username, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var personalization = await dbContext.WikiDatabaseViewPersonalizations
            .FirstOrDefaultAsync(item => item.WikiDatabaseViewId == viewId && item.Username == username, cancellationToken);
        // Only Filters/Sorts/FilterGroup are ever read back out via GetPersonalViewOverrideAsync -
        // storing the full config here (rather than a narrower shape) just reuses the existing
        // WikiDatabaseViewConfigJson serializer instead of inventing a second one.
        var storedConfig = new WikiDatabaseViewConfig(overrideConfig.Filters, overrideConfig.Sorts, null, FilterGroup: overrideConfig.FilterGroup);
        if (personalization is null)
        {
            personalization = new WikiDatabaseViewPersonalization
            {
                WikiDatabaseViewId = viewId,
                Username = username,
                ConfigJson = WikiDatabaseViewConfigJson.Serialize(storedConfig),
                CreatedAt = now,
                CreatedBy = username
            };
            await dbContext.WikiDatabaseViewPersonalizations.AddAsync(personalization, cancellationToken);
        }
        else
        {
            personalization.ConfigJson = WikiDatabaseViewConfigJson.Serialize(storedConfig);
            personalization.UpdatedAt = now;
            personalization.UpdatedBy = username;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return storedConfig;
    }

    public async Task ClearPersonalViewOverrideAsync(Guid viewId, string username, CancellationToken cancellationToken = default)
    {
        var personalization = await dbContext.WikiDatabaseViewPersonalizations
            .FirstOrDefaultAsync(item => item.WikiDatabaseViewId == viewId && item.Username == username, cancellationToken);
        if (personalization is null)
        {
            return;
        }

        dbContext.WikiDatabaseViewPersonalizations.Remove(personalization);
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

    private static WikiInlineDatabaseSnapshot BuildInlineSnapshot(
        WikiDatabase database,
        IReadOnlyList<WikiDatabaseRow>? sourceRows = null,
        IReadOnlyList<WikiDatabaseView>? sourceViews = null)
    {
        var properties = database.Properties
            .OrderBy(property => property.SortOrder)
            .Select(property => new WikiInlineDatabaseProperty(
                property.Id,
                property.Name,
                property.Type,
                property.Type is WikiDatabasePropertyTypes.CreatedTime or WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup
                    or WikiDatabasePropertyTypes.LastEditedTime or WikiDatabasePropertyTypes.LastEditedBy
                    or WikiDatabasePropertyTypes.CreatedBy or WikiDatabasePropertyTypes.Button or WikiDatabasePropertyTypes.UniqueId,
                WikiDatabasePropertyConfig.GetOptions(property)))
            .ToList();

        var rows = (sourceRows ?? database.Rows.OrderBy(row => row.SortOrder).ToList())
            .Select(row =>
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                var cells = database.Properties
                    .OrderBy(property => property.SortOrder)
                    .Select(property => new WikiInlineDatabaseCell(
                        property.Id,
                        GetInlineValue(property, values, row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy)))
                    .ToList();
                return new WikiInlineDatabaseRow(row.Id, cells);
            })
            .ToList();

        return new WikiInlineDatabaseSnapshot(
            database.Id,
            database.Title,
            string.IsNullOrWhiteSpace(database.Icon) ? "▦" : database.Icon,
            properties,
            rows)
        {
            Views = (sourceViews ?? database.Views.OrderBy(view => view.SortOrder).ToList())
                .OrderByDescending(view => view.NotionId is not null)
                .ThenBy(view => view.SortOrder)
                .Select(view =>
                {
                    var config = WikiDatabaseViewConfigJson.Parse(view.ConfigJson);
                    return new WikiInlineDatabaseView(
                        view.Id,
                        view.Name,
                        view.Type,
                        config.GroupByPropertyId);
                })
                .ToList()
        };
    }

    private static string GetInlineValue(
        WikiDatabaseProperty property,
        System.Text.Json.Nodes.JsonObject values,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt,
        string? createdBy,
        string? updatedBy) => property.Type switch
        {
            WikiDatabasePropertyTypes.Number => WikiPropertyValues.GetNumber(values, property.Id)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            WikiDatabasePropertyTypes.Checkbox => WikiPropertyValues.GetCheckbox(values, property.Id).ToString().ToLowerInvariant(),
            WikiDatabasePropertyTypes.Date => WikiPropertyValues.GetDate(values, property.Id)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Files or WikiDatabasePropertyTypes.Relation =>
                string.Join(',', WikiPropertyValues.GetMultiSelect(values, property.Id)),
            WikiDatabasePropertyTypes.CreatedTime => createdAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WikiDatabasePropertyTypes.LastEditedTime => (updatedAt ?? createdAt).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WikiDatabasePropertyTypes.CreatedBy => createdBy ?? string.Empty,
            WikiDatabasePropertyTypes.LastEditedBy => updatedBy ?? createdBy ?? string.Empty,
            WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup or WikiDatabasePropertyTypes.Button
                or WikiDatabasePropertyTypes.UniqueId or WikiDatabasePropertyTypes.Verification =>
                WikiPropertyValues.GetDisplayText(property, values, createdAt, updatedAt, createdBy, updatedBy),
            _ => WikiPropertyValues.GetText(values, property.Id) ?? string.Empty
        };

    private async Task<int> NextViewSortOrderAsync(Guid wikiDatabaseId, CancellationToken cancellationToken)
    {
        var orders = await dbContext.WikiDatabaseViews.Where(item => item.WikiDatabaseId == wikiDatabaseId).Select(item => item.SortOrder).ToListAsync(cancellationToken);
        return orders.Count == 0 ? 0 : orders.Max() + 1;
    }

    public async Task<IReadOnlyList<WikiRevisionView>> GetRowHistoryAsync(Guid rowId, CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.WikiDatabaseRowRevisions
            .AsNoTracking()
            .Where(revision => revision.WikiDatabaseRowId == rowId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);

        return revisions
            .Select(revision => new WikiRevisionView
            {
                Id = revision.Id,
                RevisionNumber = revision.RevisionNumber,
                Label = revision.Label,
                AuthorName = revision.CreatedBy,
                When = revision.CreatedAt
            })
            .ToList();
    }

    public async Task<string?> GetRowStructuralDiffAsync(
        Guid rowId,
        Guid fromRevisionId,
        Guid toRevisionId,
        CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.WikiDatabaseRowRevisions
            .AsNoTracking()
            .Where(revision => revision.WikiDatabaseRowId == rowId && (revision.Id == fromRevisionId || revision.Id == toRevisionId))
            .ToListAsync(cancellationToken);

        var from = revisions.FirstOrDefault(revision => revision.Id == fromRevisionId);
        var to = revisions.FirstOrDefault(revision => revision.Id == toRevisionId);
        if (from is null || to is null)
        {
            return null;
        }

        return BuildStructuralDiff(WikiBlockJson.ParseBlocks(from.BlocksJson), WikiBlockJson.ParseBlocks(to.BlocksJson));
    }

    public async Task<WikiDatabaseRow> RevertRowToRevisionAsync(
        Guid wikiDatabaseId,
        Guid rowId,
        Guid revisionId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var revision = await dbContext.WikiDatabaseRowRevisions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == revisionId && item.WikiDatabaseRowId == rowId, cancellationToken)
            ?? throw new InvalidOperationException("That revision no longer exists.");
        var parentRowId = await dbContext.WikiDatabaseRows.AsNoTracking()
            .Where(row => row.Id == rowId && row.WikiDatabaseId == wikiDatabaseId)
            .Select(row => row.ParentRowId)
            .FirstOrDefaultAsync(cancellationToken);

        // Revert only restores the page body, matching WikiService.RevertToRevisionAsync -
        // icon/cover are left as null (preserve current) rather than pulled from the revision.
        return await SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor
        {
            Id = rowId,
            ParentRowId = parentRowId,
            BlocksJson = revision.BlocksJson
        }, performedBy, cancellationToken);
    }

    // Mirrors WikiService.BuildStructuralDiff exactly - a dictionary-by-block-id diff over
    // two WikiBlock snapshots, independent of whether the page is a WikiPage or a row.
    private static string BuildStructuralDiff(IReadOnlyList<WikiBlock> from, IReadOnlyList<WikiBlock> to)
    {
        var fromById = from.ToDictionary(block => block.Id);
        var toById = to.ToDictionary(block => block.Id);
        var lines = new List<string>();

        foreach (var block in from)
        {
            if (!toById.ContainsKey(block.Id))
            {
                lines.Add($"- [{block.Type}] {WikiBlockHtmlRenderer.PlainTextPreview(block)}");
            }
        }

        foreach (var block in to)
        {
            if (!fromById.TryGetValue(block.Id, out var previous))
            {
                lines.Add($"+ [{block.Type}] {WikiBlockHtmlRenderer.PlainTextPreview(block)}");
            }
            else if (!string.Equals(WikiBlockJson.Serialize([previous]), WikiBlockJson.Serialize([block]), StringComparison.Ordinal))
            {
                lines.Add($"~ [{block.Type}] {WikiBlockHtmlRenderer.PlainTextPreview(block)}");
            }
        }

        return string.Join('\n', lines);
    }

    private async Task CreateRowRevisionAsync(WikiDatabaseRow row, string performedBy, CancellationToken cancellationToken)
    {
        var nextNumber = await dbContext.WikiDatabaseRowRevisions
            .Where(revision => revision.WikiDatabaseRowId == row.Id)
            .Select(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken) is { Count: > 0 } numbers
                ? numbers.Max() + 1
                : 1;

        await dbContext.WikiDatabaseRowRevisions.AddAsync(new WikiDatabaseRowRevision
        {
            WikiDatabaseRowId = row.Id,
            RevisionNumber = nextNumber,
            BlocksJson = row.BlocksJson,
            Icon = row.Icon,
            CoverImageUrl = row.CoverImageUrl,
            CreatedBy = performedBy
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await TrimOldRowRevisionsAsync(row.Id, cancellationToken);
    }

    private async Task TrimOldRowRevisionsAsync(Guid rowId, CancellationToken cancellationToken)
    {
        var revisions = await dbContext.WikiDatabaseRowRevisions
            .Where(revision => revision.WikiDatabaseRowId == rowId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);

        if (revisions.Count <= MaxRevisionsPerRow)
        {
            return;
        }

        var toDelete = revisions.Skip(MaxRevisionsPerRow).ToList();
        dbContext.WikiDatabaseRowRevisions.RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
