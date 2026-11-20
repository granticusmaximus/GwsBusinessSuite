namespace GwsBusinessSuite.WorldNewsKit;

// World News features (20-11-26): stories matching a search (people and organisation pages,
// watchlist dossiers), front pages by country, and who gets the morning briefing.
public sealed record FrontPage(string Country, IReadOnlyList<(EventOverview Story, int Outlets)> Leads);

public static class FrontPages
{
    // Countries with their own newsrooms in the feeds, in a fixed order so the page reads the same.
    public static readonly IReadOnlyList<string> Countries =
    [
        "United States", "United Kingdom", "Canada", "France", "Germany", "Spain", "Italy", "Russia", "Ukraine", "Turkey",
        "Israel", "Iran", "India", "Pakistan", "China", "Japan", "South Korea", "Australia", "Brazil", "Mexico",
        "Argentina", "South Africa", "Nigeria", "Kenya",
    ];

    public const int LeadsPerCountry = 3;
}

public sealed partial class WorldNewsStore
{
    // Stories whose reports match a search (FullTextQuery syntax), most recently active first.
    public IReadOnlyList<EventOverview> StoriesMatching(string query, DateTimeOffset since, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var ids = new List<string>();
        using (var connection = Open())
        {
            var fullText = IsFullTextBuilt(connection);
            var q = fullText ? FullTextQuery.From(query) : query.Trim();
            if (q is null) return [];
            var match = fullText
                ? "a.rowid IN (SELECT rowid FROM articles_fts WHERE articles_fts MATCH $q)"
                : "instr(lower(a.title || ' ' || COALESCE(a.snippet, '')), lower($q)) > 0";
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT a.event_id, MAX(a.seen_at) AS latest FROM articles a "
                + $"WHERE a.event_id IS NOT NULL AND a.seen_at > $since AND {match} GROUP BY a.event_id ORDER BY latest DESC LIMIT $limit";
            command.Parameters.AddWithValue("$q", q);
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return ids.Select(Event).OfType<EventOverview>().ToList();
    }

    // For each country, the stories most of its own outlets carried since a time.
    public IReadOnlyList<FrontPage> FrontPagesSince(DateTimeOffset since, IReadOnlyList<string> countries, int leads = FrontPages.LeadsPerCountry)
    {
        var pages = new List<FrontPage>();
        foreach (var country in countries)
        {
            var rows = new List<(string Id, int Outlets)>();
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT event_id, COUNT(DISTINCT outlet) AS outlets FROM articles "
                    + "WHERE country = $country AND seen_at > $since AND event_id IS NOT NULL "
                    + "GROUP BY event_id ORDER BY outlets DESC, MAX(seen_at) DESC LIMIT $limit";
                command.Parameters.AddWithValue("$country", country);
                command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$limit", leads);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1)));
            }
            var stories = rows.Select(r => (Story: Event(r.Id), r.Outlets)).Where(r => r.Story is not null).Select(r => (r.Story!, r.Outlets)).ToList();
            if (stories.Count > 0) pages.Add(new FrontPage(country, stories));
        }
        return pages;
    }

    // The morning briefing (20-11-26): who asked for it.
    private const string BriefingTable = "CREATE TABLE IF NOT EXISTS briefing_subscribers (username TEXT PRIMARY KEY, created_at INTEGER NOT NULL, last_built INTEGER);";

    public bool WantsBriefing(string username)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = BriefingTable + "SELECT COUNT(*) FROM briefing_subscribers WHERE username = $username";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        return (long)command.ExecuteScalar()! > 0;
    }

    public void SetBriefing(string username, bool on, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = BriefingTable + (on
            ? "INSERT OR IGNORE INTO briefing_subscribers (username, created_at) VALUES ($username, $created)"
            : "DELETE FROM briefing_subscribers WHERE username = $username");
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    // Subscribers with when their briefing was last built.
    public IReadOnlyList<(string Username, DateTimeOffset? LastBuilt)> BriefingSubscribers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = BriefingTable + "SELECT username, last_built FROM briefing_subscribers ORDER BY username";
        using var reader = command.ExecuteReader();
        var list = new List<(string, DateTimeOffset?)>();
        while (reader.Read()) list.Add((reader.GetString(0), reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))));
        return list;
    }

    public void MarkBriefingBuilt(string username, DateTimeOffset at)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = BriefingTable + "UPDATE briefing_subscribers SET last_built = $at WHERE username = $username";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    // Everyone with a watchlist, for their dossiers.
    public IReadOnlyList<string> KeywordWatchers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT username FROM keyword_watches ORDER BY username";
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }
}
