using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ThreatIntel;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class ThreatIntelWorkspaceService(IAppDbContext dbContext, IWikiService wikiService, TimeProvider timeProvider)
    : IThreatIntelWorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int KeptInvestigations = 200;

    public async Task<ThreatInvestigationView> SaveInvestigationAsync(string query, IndicatorKind kind, string summary,
        ThreatInvestigationSnapshot snapshot, string username, CancellationToken cancellationToken = default)
    {
        var entity = new ThreatInvestigation
        {
            Query = Truncate(query.Trim(), 500),
            Kind = kind.ToString(),
            Summary = Truncate(summary, 1000),
            ResultJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedBy = username,
            CreatedAt = timeProvider.GetUtcNow()
        };
        dbContext.ThreatInvestigations.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await PruneAsync(cancellationToken);
        return ToView(entity);
    }

    public async Task<IReadOnlyList<ThreatInvestigationView>> ListInvestigationsAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        // SQLite can't ORDER BY a DateTimeOffset column - sort after materializing.
        var rows = await dbContext.ThreatInvestigations.AsNoTracking()
            .Select(x => new ThreatInvestigationView(x.Id, x.Query, x.Kind, x.Summary, x.CreatedBy, x.CreatedAt, x.ExportedWikiPageId))
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, KeptInvestigations)).ToList();
    }

    public async Task<ThreatInvestigationSnapshot?> GetInvestigationSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var json = await dbContext.ThreatInvestigations.AsNoTracking()
            .Where(x => x.Id == id).Select(x => x.ResultJson).FirstOrDefaultAsync(cancellationToken);
        return json is null ? null : Deserialize(json);
    }

    public async Task DeleteInvestigationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ThreatInvestigations.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return;
        dbContext.ThreatInvestigations.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> ExportInvestigationToSentinelAsync(Guid id, string username, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ThreatInvestigations.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That investigation no longer exists.");

        if (entity.ExportedWikiPageId is { } existingId
            && await dbContext.WikiPages.AnyAsync(page => page.Id == existingId && page.TrashedAt == null, cancellationToken))
        {
            return existingId;
        }

        var snapshot = Deserialize(entity.ResultJson) ?? new ThreatInvestigationSnapshot(null, null, null, []);
        var markdown = ThreatInvestigationMarkdown.Build(entity.Query, entity.Summary, entity.CreatedAt, entity.CreatedBy, snapshot);
        var title = $"Investigation: {ThreatInvestigationMarkdown.Defang(entity.Query)} ({entity.CreatedAt:yyyy-MM-dd})";
        var page = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = Truncate(title, 200),
            Icon = "🛡️",
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(markdown))
        }, username, cancellationToken: cancellationToken);

        entity.ExportedWikiPageId = page.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
        return page.Id;
    }

    public async Task<DateTimeOffset?> TouchKevVisitAsync(string username, CancellationToken cancellationToken = default)
    {
        var state = await dbContext.ThreatIntelUserStates.FirstOrDefaultAsync(x => x.Username == username, cancellationToken);
        var previous = state?.LastKevVisitAt;
        if (state is null)
        {
            state = new ThreatIntelUserState { Username = username, CreatedBy = username };
            dbContext.ThreatIntelUserStates.Add(state);
        }

        state.LastKevVisitAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return previous;
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        var all = await dbContext.ThreatInvestigations.Select(x => new { x.Id, x.CreatedAt }).ToListAsync(cancellationToken);
        if (all.Count <= KeptInvestigations) return;
        var stale = all.OrderByDescending(x => x.CreatedAt).Skip(KeptInvestigations).Select(x => x.Id).ToHashSet();
        dbContext.ThreatInvestigations.RemoveRange(dbContext.ThreatInvestigations.Where(x => stale.Contains(x.Id)));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ThreatInvestigationSnapshot? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ThreatInvestigationSnapshot>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static ThreatInvestigationView ToView(ThreatInvestigation x) =>
        new(x.Id, x.Query, x.Kind, x.Summary, x.CreatedBy, x.CreatedAt, x.ExportedWikiPageId);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
