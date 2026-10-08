using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record OverwatchTripView(Guid Id, string Name, IReadOnlyList<string> Stops, DateTimeOffset UpdatedAt);

// Saved route-watch trips, private to each admin. Saving under an existing name replaces it.
public sealed class OverwatchTripService(IAppDbContextFactory dbContextFactory, TimeProvider timeProvider)
{
    public const int MaxTripsPerUser = 30;

    public async Task<IReadOnlyList<OverwatchTripView>> ListAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.OverwatchTrips.AsNoTracking().Where(t => t.Username == username).ToListAsync(ct);
        return rows.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(ToView).ToList();
    }

    public async Task<OverwatchTripView> SaveAsync(string username, string name, IReadOnlyList<string> stops, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Give the trip a name (up to 80 characters).");
        var cleaned = (stops ?? []).Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0).ToList();
        if (cleaned.Count < 2) throw new ArgumentException("A trip needs a start and a destination.");
        if (cleaned.Count > RouteWatchService.MaxStops) throw new ArgumentException($"A trip can have up to {RouteWatchService.MaxStops} stops.");
        if (cleaned.Any(s => s.Length > 200)) throw new ArgumentException("Each stop must be under 200 characters.");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow();
        var existing = (await db.OverwatchTrips.Where(t => t.Username == username).ToListAsync(ct))
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (await db.OverwatchTrips.CountAsync(t => t.Username == username, ct) >= MaxTripsPerUser)
                throw new ArgumentException($"You can save up to {MaxTripsPerUser} trips.");
            existing = new OverwatchTrip { Username = username, Name = name, CreatedBy = username, CreatedAt = now };
            db.OverwatchTrips.Add(existing);
        }
        existing.StopsJson = JsonSerializer.Serialize(cleaned);
        existing.UpdatedAt = now;
        existing.UpdatedBy = username;
        await db.SaveChangesAsync(ct);
        return ToView(existing);
    }

    public async Task DeleteAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.OverwatchTrips.Where(t => t.Id == id && t.Username == username).ExecuteDeleteAsync(ct);
    }

    private static OverwatchTripView ToView(OverwatchTrip t) =>
        new(t.Id, t.Name, JsonSerializer.Deserialize<List<string>>(t.StopsJson) ?? [], t.UpdatedAt ?? t.CreatedAt);
}
