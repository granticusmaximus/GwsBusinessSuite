namespace GwsBusinessSuite.WorldNewsKit;

// A personal Today (20-11-26): the countries and kinds of story someone pins lead their Today tab.
public sealed record TodayPin(string Kind, string Value);

public static class TodayPins
{
    public const string Country = "country";
    public const string Kind = "kind";
    public const int MaxPins = 24;

    // The pinned first, each group keeping its own order.
    public static IReadOnlyList<T> Lead<T>(IEnumerable<T> items, Func<T, bool> pinned)
    {
        var list = items.ToList();
        return list.Where(pinned).Concat(list.Where(item => !pinned(item))).ToList();
    }
}

public sealed partial class WorldNewsStore
{
    private const string TodayPinsTable = "CREATE TABLE IF NOT EXISTS today_pins (username TEXT NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (username, kind, value));";

    public IReadOnlyList<TodayPin> TodayPinsFor(string username)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = TodayPinsTable + "SELECT kind, value FROM today_pins WHERE username = $username ORDER BY created_at, value";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        using var reader = command.ExecuteReader();
        var pins = new List<TodayPin>();
        while (reader.Read()) pins.Add(new TodayPin(reader.GetString(0), reader.GetString(1)));
        return pins;
    }

    public void SetTodayPin(string username, string kind, string value, bool on, DateTimeOffset now)
    {
        if (kind is not (TodayPins.Country or TodayPins.Kind) || string.IsNullOrWhiteSpace(value)) return;
        if (on && TodayPinsFor(username).Count >= TodayPins.MaxPins) return;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = TodayPinsTable + (on
            ? "INSERT OR IGNORE INTO today_pins (username, kind, value, created_at) VALUES ($username, $kind, $value, $created)"
            : "DELETE FROM today_pins WHERE username = $username AND kind = $kind AND value = $value");
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$value", value.Trim().Length > 120 ? value.Trim()[..120] : value.Trim());
        command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }
}
