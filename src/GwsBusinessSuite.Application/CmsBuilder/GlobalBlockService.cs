using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class GlobalBlockService(IAppDbContext dbContext) : IGlobalBlockService
{
    public async Task<IReadOnlyList<GlobalBlock>> ListAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var blocks = await dbContext.GlobalBlocks
            .AsNoTracking()
            .Where(block => block.SiteId == siteId)
            .ToListAsync(cancellationToken);

        return blocks
            .OrderBy(block => block.Kind == GlobalBlockKinds.Section ? 0 : 1)
            .ThenBy(block => block.Name)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, GlobalBlock>> GetByIdsAsync(
        Guid siteId,
        IEnumerable<Guid> globalBlockIds,
        CancellationToken cancellationToken = default)
    {
        var ids = globalBlockIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, GlobalBlock>();
        }

        var blocks = await dbContext.GlobalBlocks
            .AsNoTracking()
            .Where(block => block.SiteId == siteId && ids.Contains(block.Id))
            .ToListAsync(cancellationToken);

        return blocks.ToDictionary(block => block.Id);
    }

    public async Task<GlobalBlock> CreateWidgetAsync(
        Guid siteId,
        string name,
        LayoutWidget widget,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = ValidateName(name);
        var now = DateTimeOffset.UtcNow;
        var canonical = GlobalBlockMaterializer.ToCanonicalWidget(widget);
        var globalBlock = new GlobalBlock
        {
            SiteId = siteId,
            Name = trimmedName,
            Kind = GlobalBlockKinds.Widget,
            WidgetType = canonical.WidgetType,
            Json = CmsBuilderJson.Serialize(canonical),
            CreatedAt = now,
            CreatedBy = "cms-global-block",
            UpdatedAt = now,
            UpdatedBy = "cms-global-block"
        };

        await dbContext.GlobalBlocks.AddAsync(globalBlock, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return globalBlock;
    }

    public async Task<GlobalBlock> CreateSectionAsync(
        Guid siteId,
        string name,
        LayoutSection section,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = ValidateName(name);
        var now = DateTimeOffset.UtcNow;
        var canonical = GlobalBlockMaterializer.ToCanonicalSection(section);
        var globalBlock = new GlobalBlock
        {
            SiteId = siteId,
            Name = trimmedName,
            Kind = GlobalBlockKinds.Section,
            WidgetType = null,
            Json = CmsBuilderJson.Serialize(canonical),
            CreatedAt = now,
            CreatedBy = "cms-global-block",
            UpdatedAt = now,
            UpdatedBy = "cms-global-block"
        };

        await dbContext.GlobalBlocks.AddAsync(globalBlock, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return globalBlock;
    }

    public async Task<GlobalBlock> SyncWidgetAsync(
        Guid siteId,
        LayoutWidget widget,
        CancellationToken cancellationToken = default)
    {
        if (widget.GlobalBlockId is not { } globalBlockId)
        {
            throw new InvalidOperationException("Only widgets with GlobalBlockId can be synced.");
        }

        var globalBlock = await dbContext.GlobalBlocks
            .FirstOrDefaultAsync(block => block.Id == globalBlockId && block.SiteId == siteId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var isNew = globalBlock is null;
        globalBlock ??= new GlobalBlock
        {
            Id = globalBlockId,
            SiteId = siteId,
            Name = BuildFallbackName(GlobalBlockKinds.Widget, widget.WidgetType),
            CreatedAt = now,
            CreatedBy = "cms-global-block"
        };

        // Once a field is flagged overridable, no single placement's save can change the
        // shared/canonical value for it anymore - each placement owns its own divergent value
        // in its own Overrides instead (see LayoutWidget.Overrides,
        // GlobalBlockMaterializer.ApplyResolvedWidget). Preserve whatever the canonical
        // currently holds for those keys rather than letting this placement's in-memory Props
        // (which, for an overridden key, holds THIS placement's own value, not the shared one)
        // silently become the new shared default.
        var overridableFields = GlobalBlockOverridableFields.Parse(globalBlock.OverridableFieldsJson);
        var widgetToSync = widget;
        if (overridableFields.Count > 0)
        {
            var existingCanonical = GlobalBlockMaterializer.DeserializeWidget(globalBlock.Json);
            var syncedProps = new Dictionary<string, string>(widget.Props);
            foreach (var key in overridableFields)
            {
                if (existingCanonical is not null && existingCanonical.Props.TryGetValue(key, out var canonicalValue))
                {
                    syncedProps[key] = canonicalValue;
                }
                else
                {
                    syncedProps.Remove(key);
                }
            }
            widgetToSync = new LayoutWidget
            {
                Id = widget.Id,
                GlobalBlockId = widget.GlobalBlockId,
                WidgetType = widget.WidgetType,
                Props = syncedProps,
                Style = widget.Style,
                Visibility = widget.Visibility,
                EditPermission = widget.EditPermission,
                Overrides = widget.Overrides
            };
        }

        var canonical = GlobalBlockMaterializer.ToCanonicalWidget(widgetToSync);
        globalBlock.Kind = GlobalBlockKinds.Widget;
        globalBlock.WidgetType = canonical.WidgetType;
        globalBlock.Json = CmsBuilderJson.Serialize(canonical);
        globalBlock.UpdatedAt = now;
        globalBlock.UpdatedBy = "cms-global-block";

        if (isNew)
        {
            await dbContext.GlobalBlocks.AddAsync(globalBlock, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return globalBlock;
    }

    public async Task<GlobalBlock> SyncSectionAsync(
        Guid siteId,
        LayoutSection section,
        CancellationToken cancellationToken = default)
    {
        if (section.GlobalBlockId is not { } globalBlockId)
        {
            throw new InvalidOperationException("Only sections with GlobalBlockId can be synced.");
        }

        var globalBlock = await dbContext.GlobalBlocks
            .FirstOrDefaultAsync(block => block.Id == globalBlockId && block.SiteId == siteId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var canonical = GlobalBlockMaterializer.ToCanonicalSection(section);
        var isNew = globalBlock is null;
        globalBlock ??= new GlobalBlock
        {
            Id = globalBlockId,
            SiteId = siteId,
            Name = BuildFallbackName(GlobalBlockKinds.Section, null),
            CreatedAt = now,
            CreatedBy = "cms-global-block"
        };

        globalBlock.Kind = GlobalBlockKinds.Section;
        globalBlock.WidgetType = null;
        globalBlock.Json = CmsBuilderJson.Serialize(canonical);
        globalBlock.UpdatedAt = now;
        globalBlock.UpdatedBy = "cms-global-block";

        if (isNew)
        {
            await dbContext.GlobalBlocks.AddAsync(globalBlock, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return globalBlock;
    }

    public async Task RenameAsync(
        Guid siteId,
        Guid globalBlockId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var globalBlock = await dbContext.GlobalBlocks
            .FirstOrDefaultAsync(block => block.Id == globalBlockId && block.SiteId == siteId, cancellationToken);
        if (globalBlock is null)
        {
            throw new InvalidOperationException("Global block not found.");
        }

        globalBlock.Name = ValidateName(name);
        globalBlock.UpdatedAt = DateTimeOffset.UtcNow;
        globalBlock.UpdatedBy = "cms-global-block";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetOverridableFieldsAsync(
        Guid siteId,
        Guid globalBlockId,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken = default)
    {
        var globalBlock = await dbContext.GlobalBlocks
            .FirstOrDefaultAsync(block => block.Id == globalBlockId && block.SiteId == siteId, cancellationToken);
        if (globalBlock is null)
        {
            throw new InvalidOperationException("Global block not found.");
        }

        // Only Widget-kind blocks have Props to flag fields on - Section blocks have typed
        // fields (Label/Background/etc.), not a Props dictionary, so per-instance overrides
        // aren't offered for sections in this first version.
        if (globalBlock.Kind != GlobalBlockKinds.Widget)
        {
            throw new InvalidOperationException("Only widget-kind global blocks support per-instance overridable fields.");
        }

        var candidates = GlobalBlockOverridableFields.CandidatesFor(globalBlock.WidgetType ?? string.Empty);
        var validated = fields.Where(field => candidates.Contains(field, StringComparer.Ordinal)).ToList();
        globalBlock.OverridableFieldsJson = GlobalBlockOverridableFields.Serialize(validated);
        globalBlock.UpdatedAt = DateTimeOffset.UtcNow;
        globalBlock.UpdatedBy = "cms-global-block";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        Guid siteId,
        Guid globalBlockId,
        CancellationToken cancellationToken = default)
    {
        var globalBlock = await dbContext.GlobalBlocks
            .FirstOrDefaultAsync(block => block.Id == globalBlockId && block.SiteId == siteId, cancellationToken);
        if (globalBlock is null)
        {
            return;
        }

        // Pages that placed this block keep their own last-synced copy of its content
        // in their own layout JSON (see GlobalBlockResolver) - deleting the shared
        // source here just stops future syncs from propagating, it doesn't touch
        // any page that already used it.
        dbContext.GlobalBlocks.Remove(globalBlock);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ValidateName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Global block name is required.");
        }

        return trimmed;
    }

    private static string BuildFallbackName(string kind, string? widgetType) =>
        kind == GlobalBlockKinds.Section
            ? "Recovered Section"
            : $"Recovered {HumanizeWidgetType(widgetType)}";

    private static string HumanizeWidgetType(string? widgetType)
    {
        var value = string.IsNullOrWhiteSpace(widgetType) ? "Widget" : widgetType.Trim();
        return string.Join(' ', value
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
