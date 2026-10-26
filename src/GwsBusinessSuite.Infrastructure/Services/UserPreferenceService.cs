using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// A DbContext per call from the factory: the layout reads the density while the page renders,
// and the account menu's toggle writes it from its own island on the same circuit.
public sealed class UserPreferenceService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time) : IUserPreferenceService
{
    public async Task<string> GetAsync(string username, string key, CancellationToken ct = default)
    {
        var fallback = UserPreferenceRules.DefaultFor(key);
        if (string.IsNullOrWhiteSpace(username) || username == "unknown") return fallback;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var value = await db.UserPreferences.AsNoTracking()
            .Where(preference => preference.Username == username && preference.Key == key)
            .Select(preference => preference.Value)
            .FirstOrDefaultAsync(ct);
        return value is not null && UserPreferenceRules.IsAllowed(key, value) ? value : fallback;
    }

    public async Task SetAsync(string username, string key, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || username == "unknown") throw new ArgumentException("Sign in to change your settings.");
        if (!UserPreferenceRules.IsAllowed(key, value)) throw new ArgumentException($"'{value}' isn't a setting for {key}.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.UserPreferences.SingleOrDefaultAsync(
            preference => preference.Username == username && preference.Key == key, ct);
        if (existing is null)
        {
            db.UserPreferences.Add(new UserPreference { Username = username, Key = key, Value = value, CreatedBy = username, CreatedAt = time.GetUtcNow() });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = time.GetUtcNow();
            existing.UpdatedBy = username;
        }
        await db.SaveChangesAsync(ct);
    }
}
