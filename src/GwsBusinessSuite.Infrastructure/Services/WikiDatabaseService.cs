using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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

    public async Task<IReadOnlyList<WikiDatabase>> ListDatabasesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.WikiDatabases.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<WikiDatabase?> GetDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default)
    {
        var database = await dbContext.WikiDatabases.AsNoTracking()
            .Include(item => item.Properties)
            .Include(item => item.Rows)
            .Include(item => item.Views)
            .FirstOrDefaultAsync(item => item.Id == wikiDatabaseId, cancellationToken);
        if (database is null)
        {
            return null;
        }

        var relatedDatabaseIds = database.Properties
            .Where(property => property.Type == WikiDatabasePropertyTypes.Relation)
            .Select(property => WikiDatabasePropertyConfig.Parse(property).RelatedDatabaseId)
            .Where(id => id.HasValue && id.Value != wikiDatabaseId)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var relatedDatabases = relatedDatabaseIds.Count == 0
            ? []
            : await dbContext.WikiDatabases.AsNoTracking()
                .Where(item => relatedDatabaseIds.Contains(item.Id))
                .Include(item => item.Properties)
                .Include(item => item.Rows)
                .ToListAsync(cancellationToken);

        WikiDatabaseComputation.Materialize(database, relatedDatabases.ToDictionary(item => item.Id));
        return database;
    }

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
                SortOrder = row.SortOrder,
                PropertyValuesJson = WikiPropertyValues.Serialize(remappedValues),
                BlocksJson = WikiBlockJson.Serialize(blocks),
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
                        .ToDictionary(item => RemapPropertyId(item.Key, propertyIds), item => item.Value))),
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
                        .ToDictionary(item => RemapPropertyId(item.Key, propertyIds), item => item.Value))),
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
            editor.Type is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.MultiSelect ? editor.Options : [],
            string.IsNullOrWhiteSpace(editor.FormulaExpression) ? null : editor.FormulaExpression.Trim(),
            editor.RelatedDatabaseId,
            editor.ReciprocalPropertyId,
            editor.RelationPropertyId,
            editor.RollupPropertyId,
            string.IsNullOrWhiteSpace(editor.RollupAggregation) ? null : editor.RollupAggregation);
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

        var computedPropertyIds = await dbContext.WikiDatabaseProperties
            .Where(property => property.WikiDatabaseId == wikiDatabaseId
                && (property.Type == WikiDatabasePropertyTypes.Formula || property.Type == WikiDatabasePropertyTypes.Rollup))
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

    public async Task DeleteRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default)
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
            property.Id == groupByPropertyId && property.Type == WikiDatabasePropertyTypes.Select)
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
        if (property.Type is WikiDatabasePropertyTypes.CreatedTime or WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup)
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
                var selectedId = WikiDatabasePropertyConfig.GetOptions(property)
                    .Any(option => option.Id == value) ? value : null;
                WikiPropertyValues.SetText(values, property.Id, selectedId);
                break;
            default:
                WikiPropertyValues.SetText(values, property.Id, value);
                break;
        }

        await SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor
        {
            Id = rowId,
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

    private static WikiInlineDatabaseSnapshot BuildInlineSnapshot(WikiDatabase database)
    {
        var properties = database.Properties
            .OrderBy(property => property.SortOrder)
            .Select(property => new WikiInlineDatabaseProperty(
                property.Id,
                property.Name,
                property.Type,
                property.Type is WikiDatabasePropertyTypes.CreatedTime or WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup,
                WikiDatabasePropertyConfig.GetOptions(property)))
            .ToList();

        var rows = database.Rows
            .OrderBy(row => row.SortOrder)
            .Select(row =>
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                var cells = database.Properties
                    .OrderBy(property => property.SortOrder)
                    .Select(property => new WikiInlineDatabaseCell(
                        property.Id,
                        GetInlineValue(property, values, row.CreatedAt)))
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
            Views = database.Views
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
        DateTimeOffset createdAt) => property.Type switch
        {
            WikiDatabasePropertyTypes.Number => WikiPropertyValues.GetNumber(values, property.Id)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            WikiDatabasePropertyTypes.Checkbox => WikiPropertyValues.GetCheckbox(values, property.Id).ToString().ToLowerInvariant(),
            WikiDatabasePropertyTypes.Date => WikiPropertyValues.GetDate(values, property.Id)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Person or WikiDatabasePropertyTypes.Files or WikiDatabasePropertyTypes.Relation =>
                string.Join(',', WikiPropertyValues.GetMultiSelect(values, property.Id)),
            WikiDatabasePropertyTypes.CreatedTime => createdAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup =>
                WikiPropertyValues.GetDisplayText(property, values, createdAt),
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

        // Revert only restores the page body, matching WikiService.RevertToRevisionAsync -
        // icon/cover are left as null (preserve current) rather than pulled from the revision.
        return await SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor
        {
            Id = rowId,
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
