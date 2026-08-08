using GwsBusinessSuite.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CmsBuilder;

// logger is optional (defaults to null, treated as no-op below) purely so existing test call
// sites that construct this directly don't all need a fake logger wired through them.
// Production DI always resolves the real registered instance.
public sealed class GlobalBlockResolver(IGlobalBlockService globalBlockService, ILogger<GlobalBlockResolver>? logger = null)
{
    private const int MaxDepth = 5;

    public async Task ResolveAsync(Guid siteId, PageLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        await ResolveSectionsAsync(siteId, layout.Sections, depth: 0, cancellationToken);
    }

    private async Task ResolveSectionsAsync(
        Guid siteId,
        IReadOnlyList<LayoutSection> sections,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxDepth || sections.Count == 0)
        {
            return;
        }

        var sectionBlocks = await FetchBlocksAsync(
            siteId,
            sections.Where(section => section.GlobalBlockId is not null).Select(section => section.GlobalBlockId!.Value),
            cancellationToken);

        foreach (var section in sections)
        {
            if (section.GlobalBlockId is not { } globalBlockId)
            {
                continue;
            }

            if (sectionBlocks.TryGetValue(globalBlockId, out var globalBlock) && globalBlock.Kind == GlobalBlockKinds.Section)
            {
                var canonical = GlobalBlockMaterializer.DeserializeSection(globalBlock.Json);
                if (canonical is not null)
                {
                    GlobalBlockMaterializer.ApplyResolvedSection(section, canonical);
                }
            }
            else
            {
                // Graceful by design (the page keeps rendering with whatever section content
                // was last cached), but that silence meant an editor had no way to discover a
                // linked global block had been deleted or retyped out from under them.
                logger?.LogWarning(
                    "Site {SiteId} references global section block {GlobalBlockId}, which no longer exists or " +
                    "changed kind - rendering the section's last-known content instead.",
                    siteId, globalBlockId);
            }
        }

        foreach (var section in sections)
        {
            await ResolveWidgetsAsync(siteId, section.Columns.SelectMany(column => column.Widgets).ToList(), depth + 1, cancellationToken);
        }
    }

    private async Task ResolveWidgetsAsync(
        Guid siteId,
        IReadOnlyList<LayoutWidget> widgets,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxDepth || widgets.Count == 0)
        {
            return;
        }

        var widgetBlocks = await FetchBlocksAsync(
            siteId,
            widgets.Where(widget => widget.GlobalBlockId is not null).Select(widget => widget.GlobalBlockId!.Value),
            cancellationToken);

        foreach (var widget in widgets)
        {
            if (widget.GlobalBlockId is not { } globalBlockId)
            {
                continue;
            }

            if (widgetBlocks.TryGetValue(globalBlockId, out var globalBlock) && globalBlock.Kind == GlobalBlockKinds.Widget)
            {
                var canonical = GlobalBlockMaterializer.DeserializeWidget(globalBlock.Json);
                if (canonical is not null)
                {
                    GlobalBlockMaterializer.ApplyResolvedWidget(widget, canonical);
                }
            }
            else
            {
                logger?.LogWarning(
                    "Site {SiteId} references global widget block {GlobalBlockId}, which no longer exists or " +
                    "changed kind - rendering the widget's last-known content instead.",
                    siteId, globalBlockId);
            }
        }
    }

    private async Task<IReadOnlyDictionary<Guid, GlobalBlock>> FetchBlocksAsync(
        Guid siteId,
        IEnumerable<Guid> globalBlockIds,
        CancellationToken cancellationToken)
    {
        var ids = globalBlockIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, GlobalBlock>();
        }

        return await globalBlockService.GetByIdsAsync(siteId, ids, cancellationToken);
    }
}
