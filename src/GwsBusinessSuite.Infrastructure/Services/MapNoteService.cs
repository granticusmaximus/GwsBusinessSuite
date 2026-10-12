using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public static class MapNoteKinds
{
    public const string Pin = "pin";
    public const string Area = "area";
}

public sealed record MapNotePoint(double Latitude, double Longitude);

public sealed record MapNoteView(Guid Id, string Owner, string Kind, string Title, string Body, string Color,
    IReadOnlyList<MapNotePoint> Points, bool IsShared, DateTimeOffset UpdatedAt);

// Overwatch map notes: pins and drawn areas with a title and note, per user. Sharing makes a
// random key; a link with the note's id and that key opens it (read-only) for any signed-in
// admin. Unsharing removes the key, so old links stop working.
public sealed partial class MapNoteService(IAppDbContextFactory dbContextFactory, TimeProvider timeProvider)
{
    public const int MaxNotesPerUser = 500;
    public const int MaxAreaPoints = 100;
    public static readonly string[] Colors = ["#ffd166", "#ff4d4d", "#7dffb0", "#4dabff", "#c77dff"];

    public async Task<IReadOnlyList<MapNoteView>> ListAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.OverwatchMapNotes.AsNoTracking().Where(n => n.Username == username).ToListAsync(ct);
        return rows.OrderByDescending(n => n.UpdatedAt ?? n.CreatedAt).Select(ToView).ToList();
    }

    public async Task<MapNoteView> SaveAsync(string username, Guid? id, string kind, string title, string? body, string? color,
        IReadOnlyList<MapNotePoint> points, CancellationToken ct = default)
    {
        title = (title ?? string.Empty).Trim();
        body = (body ?? string.Empty).Trim();
        if (title.Length is 0 or > 80) throw new ArgumentException("Give the note a title (up to 80 characters).");
        if (body.Length > 2000) throw new ArgumentException("Keep the note under 2,000 characters.");
        if (kind is not (MapNoteKinds.Pin or MapNoteKinds.Area)) throw new ArgumentException("A note is a pin or an area.");
        points ??= [];
        if (points.Any(p => !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180))
            throw new ArgumentException("A point is off the map.");
        if (kind == MapNoteKinds.Pin && points.Count != 1) throw new ArgumentException("Pick one spot for a pin.");
        if (kind == MapNoteKinds.Area && points.Count is < 3 or > MaxAreaPoints)
            throw new ArgumentException($"Draw an area with 3 to {MaxAreaPoints} points.");
        color = color is not null && HexColor().IsMatch(color) ? color.ToLowerInvariant() : Colors[0];

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow();
        OverwatchMapNote? note = null;
        if (id is { } existingId)
        {
            note = await db.OverwatchMapNotes.FirstOrDefaultAsync(n => n.Id == existingId && n.Username == username, ct)
                ?? throw new ArgumentException("That note no longer exists.");
        }
        else
        {
            if (await db.OverwatchMapNotes.CountAsync(n => n.Username == username, ct) >= MaxNotesPerUser)
                throw new ArgumentException($"You can keep up to {MaxNotesPerUser} map notes.");
            note = new OverwatchMapNote { Username = username, Kind = kind, Title = title, CreatedBy = username, CreatedAt = now };
            await db.OverwatchMapNotes.AddAsync(note, ct);
        }
        note.Kind = kind;
        note.Title = title;
        note.Body = body;
        note.Color = color;
        note.PointsJson = JsonSerializer.Serialize(points.Select(p => new[] { Math.Round(p.Latitude, 6), Math.Round(p.Longitude, 6) }));
        note.UpdatedAt = now;
        note.UpdatedBy = username;
        await db.SaveChangesAsync(ct);
        return ToView(note);
    }

    public async Task DeleteAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.OverwatchMapNotes.Where(n => n.Id == id && n.Username == username).ExecuteDeleteAsync(ct);
    }

    // Returns the share key (made on first share, reused after), or null when the note isn't theirs.
    public async Task<string?> ShareAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var note = await db.OverwatchMapNotes.FirstOrDefaultAsync(n => n.Id == id && n.Username == username, ct);
        if (note is null) return null;
        if (string.IsNullOrEmpty(note.ShareKey))
        {
            note.ShareKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            await db.SaveChangesAsync(ct);
        }
        return note.ShareKey;
    }

    public async Task UnshareAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var note = await db.OverwatchMapNotes.FirstOrDefaultAsync(n => n.Id == id && n.Username == username, ct);
        if (note is null || note.ShareKey is null) return;
        note.ShareKey = null;
        await db.SaveChangesAsync(ct);
    }

    // The owner always sees their own note; anyone else needs the current share key.
    public async Task<MapNoteView?> OpenAsync(string username, Guid id, string? key, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var note = await db.OverwatchMapNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null) return null;
        if (note.Username == username) return ToView(note);
        return !string.IsNullOrEmpty(note.ShareKey) && key is not null
               && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(note.ShareKey), System.Text.Encoding.UTF8.GetBytes(key))
            ? ToView(note)
            : null;
    }

    public static string ShareLink(Guid id, string key) => $"/admin/osint?note={id}&key={key}";

    private static MapNoteView ToView(OverwatchMapNote n)
    {
        var points = (JsonSerializer.Deserialize<double[][]>(n.PointsJson) ?? [])
            .Where(p => p.Length == 2).Select(p => new MapNotePoint(p[0], p[1])).ToList();
        return new MapNoteView(n.Id, n.Username, n.Kind, n.Title, n.Body, n.Color, points, !string.IsNullOrEmpty(n.ShareKey), n.UpdatedAt ?? n.CreatedAt);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
