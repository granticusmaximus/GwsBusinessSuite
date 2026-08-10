using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class WikiSyncedBlockService(IAppDbContext dbContext) : IWikiSyncedBlockService
{
    public async Task<Guid> CreateAsync(
        IReadOnlyList<WikiRichTextSpan> initialRichText,
        Guid? originWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var source = new WikiSyncedBlockSource
        {
            RichTextJson = JsonSerializer.Serialize(initialRichText, WikiBlockJson.Options),
            OriginWikiPageId = originWikiPageId,
            CreatedAt = now,
            CreatedBy = performedBy,
            UpdatedAt = now,
            UpdatedBy = performedBy
        };
        await dbContext.WikiSyncedBlockSources.AddAsync(source, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return source.Id;
    }

    public async Task UpdateContentAsync(
        Guid sourceId,
        IReadOnlyList<WikiRichTextSpan> richText,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var source = await dbContext.WikiSyncedBlockSources
            .FirstOrDefaultAsync(item => item.Id == sourceId, cancellationToken);
        // An instance can outlive its source (e.g. a race with another deletion path); leaving
        // it untouched is safer than failing an otherwise-unrelated page save over it.
        if (source is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(richText, WikiBlockJson.Options);
        if (string.Equals(source.RichTextJson, json, StringComparison.Ordinal))
        {
            return;
        }

        source.RichTextJson = json;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        source.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<WikiRichTextSpan>>> GetContentBatchAsync(
        IReadOnlyCollection<Guid> sourceIds,
        CancellationToken cancellationToken = default)
    {
        if (sourceIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<WikiRichTextSpan>>();
        }

        var rows = await dbContext.WikiSyncedBlockSources
            .AsNoTracking()
            .Where(item => sourceIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            item => item.Id,
            item => (IReadOnlyList<WikiRichTextSpan>)(
                JsonSerializer.Deserialize<List<WikiRichTextSpan>>(item.RichTextJson, WikiBlockJson.Options) ?? []));
    }
}
