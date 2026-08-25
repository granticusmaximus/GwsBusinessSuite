using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.MindMaps;

public sealed class MindMapService(IAppDbContext db, TimeProvider timeProvider) : IMindMapService
{
    public async Task<IReadOnlyList<MindMapSummary>> ListForOwnerAsync(string ownerUsername, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var maps = await db.MindMaps
            .AsNoTracking()
            .Where(item => item.OwnerUsername == owner)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        return maps.Select(item => new MindMapSummary(item.Id, item.Title, item.UpdatedAt ?? item.CreatedAt)).ToList();
    }

    public async Task<MindMapDetail?> GetByIdAsync(string ownerUsername, Guid mindMapId, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var entity = await db.MindMaps
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == mindMapId && item.OwnerUsername == owner, cancellationToken);
        return entity is null ? null : new MindMapDetail(entity.Id, entity.Title, MindMapTreeJson.ParseRoot(entity.TreeJson));
    }

    public async Task<Guid> CreateAsync(string ownerUsername, string title, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Untitled mind map" : title.Trim();

        var nextOrder = await db.MindMaps
            .Where(item => item.OwnerUsername == owner)
            .Select(item => (int?)item.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        var entity = new MindMap
        {
            OwnerUsername = owner,
            Title = normalizedTitle,
            TreeJson = MindMapTreeJson.SerializeNewRoot(normalizedTitle),
            SortOrder = nextOrder + 1,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = owner
        };
        await db.MindMaps.AddAsync(entity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task RenameAsync(string ownerUsername, Guid mindMapId, string title, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var entity = await db.MindMaps
            .FirstOrDefaultAsync(item => item.Id == mindMapId && item.OwnerUsername == owner, cancellationToken)
            ?? throw new InvalidOperationException("Mind map was not found.");

        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length is < 1 or > 120)
        {
            throw new InvalidOperationException("Mind map title must be between 1 and 120 characters.");
        }

        entity.Title = normalizedTitle;
        entity.UpdatedAt = timeProvider.GetUtcNow();
        entity.UpdatedBy = owner;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveTreeAsync(string ownerUsername, Guid mindMapId, MindMapNode root, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var entity = await db.MindMaps
            .FirstOrDefaultAsync(item => item.Id == mindMapId && item.OwnerUsername == owner, cancellationToken)
            ?? throw new InvalidOperationException("Mind map was not found.");

        entity.TreeJson = MindMapTreeJson.Serialize(root);
        entity.UpdatedAt = timeProvider.GetUtcNow();
        entity.UpdatedBy = owner;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string ownerUsername, Guid mindMapId, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var entity = await db.MindMaps
            .FirstOrDefaultAsync(item => item.Id == mindMapId && item.OwnerUsername == owner, cancellationToken)
            ?? throw new InvalidOperationException("Mind map was not found.");

        db.MindMaps.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeOwner(string ownerUsername)
    {
        var owner = ownerUsername.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(owner)
            ? throw new InvalidOperationException("An authenticated user is required.")
            : owner;
    }
}
