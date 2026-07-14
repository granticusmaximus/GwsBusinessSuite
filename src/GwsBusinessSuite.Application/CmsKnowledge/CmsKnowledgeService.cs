using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsKnowledge;

public sealed class CmsKnowledgeService(IAppDbContext db) : ICmsKnowledgeService
{
    public async Task<IReadOnlyList<CmsKnowledgeSource>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        var sources = await db.CmsKnowledgeSources.AsNoTracking().ToListAsync(cancellationToken);
        return sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<CmsKnowledgeEntry>> ListEntriesAsync(Guid? sourceId = null, CancellationToken cancellationToken = default)
    {
        var query = db.CmsKnowledgeEntries.AsNoTracking();
        if (sourceId is { } id)
        {
            query = query.Where(e => e.SourceId == id);
        }

        var entries = await query.ToListAsync(cancellationToken);
        return entries.OrderBy(e => e.Capability, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<CmsKnowledgeQueryResult>> SearchAsync(string query, int take = 5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static x => x.ToLowerInvariant())
            .Distinct()
            .ToArray();

        var entries = await db.CmsKnowledgeEntries.AsNoTracking().ToListAsync(cancellationToken);
        var sourcesById = await db.CmsKnowledgeSources.AsNoTracking().ToDictionaryAsync(s => s.Id, cancellationToken);

        var ranked = entries
            .Select(entry => new CmsKnowledgeQueryResult
            {
                SourceId = entry.SourceId,
                SourceName = sourcesById.TryGetValue(entry.SourceId, out var source) ? source.Name : "(unknown source)",
                Capability = entry.Capability,
                WorkflowSummary = entry.WorkflowSummary,
                ImplementationHint = entry.ImplementationHint,
                SuggestedBlocks = ParseBlocks(entry.SuggestedBlocksCsv),
                Score = ScoreEntry(entry, terms)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Capability)
            .Take(Math.Clamp(take, 1, 20))
            .ToList();

        return ranked;
    }

    public async Task<CmsKnowledgeSource> SaveSourceAsync(CmsKnowledgeSourceEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (string.IsNullOrWhiteSpace(editor.Key)) throw new ArgumentException("Key is required.", nameof(editor));
        if (string.IsNullOrWhiteSpace(editor.Name)) throw new ArgumentException("Name is required.", nameof(editor));

        var now = DateTimeOffset.UtcNow;
        var source = editor.SourceId is { } sourceId
            ? await db.CmsKnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken)
            : null;

        var isNew = source is null;
        source ??= new CmsKnowledgeSource
        {
            Key = editor.Key.Trim(),
            Name = editor.Name.Trim(),
            CreatedAt = now,
            CreatedBy = "cms-knowledge"
        };

        source.Key = editor.Key.Trim();
        source.Name = editor.Name.Trim();
        source.LicenseNotes = editor.LicenseNotes?.Trim() ?? string.Empty;
        source.UsageGuidance = editor.UsageGuidance?.Trim() ?? string.Empty;
        source.UpdatedAt = now;
        source.UpdatedBy = "cms-knowledge";

        if (isNew)
        {
            await db.CmsKnowledgeSources.AddAsync(source, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var source = await db.CmsKnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return;
        }

        var entries = await db.CmsKnowledgeEntries.Where(e => e.SourceId == sourceId).ToListAsync(cancellationToken);
        db.CmsKnowledgeEntries.RemoveRange(entries);
        db.CmsKnowledgeSources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CmsKnowledgeEntry> SaveEntryAsync(CmsKnowledgeEntryEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (string.IsNullOrWhiteSpace(editor.Capability)) throw new ArgumentException("Capability is required.", nameof(editor));

        var sourceExists = await db.CmsKnowledgeSources.AnyAsync(s => s.Id == editor.SourceId, cancellationToken);
        if (!sourceExists)
        {
            throw new InvalidOperationException("The selected knowledge source no longer exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var entry = editor.EntryId is { } entryId
            ? await db.CmsKnowledgeEntries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken)
            : null;

        var isNew = entry is null;
        entry ??= new CmsKnowledgeEntry
        {
            Capability = editor.Capability.Trim(),
            CreatedAt = now,
            CreatedBy = "cms-knowledge"
        };

        entry.SourceId = editor.SourceId;
        entry.Capability = editor.Capability.Trim();
        entry.WorkflowSummary = editor.WorkflowSummary?.Trim() ?? string.Empty;
        entry.ImplementationHint = editor.ImplementationHint?.Trim() ?? string.Empty;
        entry.SuggestedBlocksCsv = editor.SuggestedBlocks?.Trim() ?? string.Empty;
        entry.UpdatedAt = now;
        entry.UpdatedBy = "cms-knowledge";

        if (isNew)
        {
            await db.CmsKnowledgeEntries.AddAsync(entry, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task DeleteEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = await db.CmsKnowledgeEntries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return;
        }

        db.CmsKnowledgeEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string[] ParseBlocks(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ScoreEntry(CmsKnowledgeEntry entry, IReadOnlyCollection<string> terms)
    {
        var score = 0;
        var capability = entry.Capability.ToLowerInvariant();
        var summary = entry.WorkflowSummary.ToLowerInvariant();
        var hint = entry.ImplementationHint.ToLowerInvariant();
        var blocks = entry.SuggestedBlocksCsv.ToLowerInvariant();

        foreach (var term in terms)
        {
            if (capability.Contains(term, StringComparison.Ordinal))
            {
                score += 5;
            }

            if (summary.Contains(term, StringComparison.Ordinal))
            {
                score += 3;
            }

            if (hint.Contains(term, StringComparison.Ordinal))
            {
                score += 2;
            }

            if (blocks.Contains(term, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }
}
