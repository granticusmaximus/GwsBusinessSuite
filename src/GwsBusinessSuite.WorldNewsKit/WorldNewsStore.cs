using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace GwsBusinessSuite.WorldNewsKit;

// An event as the feed shows it: its reports counted by distinct outlet, country and language.
// Headline is the English headline from its latest summary that has one.
public sealed record EventOverview(string Id, string Title, int ArticleCount, int OutletCount,
    IReadOnlyList<string> Countries, IReadOnlyList<string> Languages, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    string? Headline = null);

// A situation as the feed shows it: Title is from its biggest event (an English headline when
// one has one), Latest is its newest event's.
public sealed record SituationOverview(string Id, string Title, string Latest, int EventCount, int ArticleCount, int OutletCount,
    IReadOnlyList<string> Countries, IReadOnlyList<string> Languages, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    string? Kind = null);

// One version of an event's summary. Versions count up from 1; ArticleCount is how many reports
// the event had when it was written, which decides when it's due again.
public sealed record EventSummary(string EventId, int Version, string? Headline, string Text, string Model,
    int ArticleCount, DateTimeOffset CreatedAt);

public sealed record WorldNewsAlert(Guid Id, string Username, string SituationId, string Title, string Message,
    DateTimeOffset CreatedAt, bool IsRead);

// A hand correction of where Overwatch puts a situation: a country code (and optional city), or
// Hidden to keep it off the globe.
public sealed record SituationPlacement(string SituationId, string? CountryCode, string? City, bool Hidden,
    string UpdatedBy, DateTimeOffset UpdatedAt);
public sealed record WorldStory(EventOverview Event, int RecentReports, int RecentOutlets, string? SituationId);
public sealed record WorldCountry(string Name, int ReportCount, int EventCount, int OutletCount, IReadOnlyList<WorldStory> TopStories);
public sealed record WorldSnapshot(DateTimeOffset Since, IReadOnlyList<WorldCountry> Countries, IReadOnlyList<WorldStory> BiggestStories);

// The Mac app's local world-news database (SQLite). Times are stored as Unix seconds and vectors
// as little-endian float32 blobs.
public sealed class WorldNewsStore
{
    private readonly string _connectionString;

    public WorldNewsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS articles (
                id TEXT PRIMARY KEY, url TEXT NOT NULL, title TEXT NOT NULL, outlet TEXT NOT NULL,
                language TEXT NOT NULL, country TEXT NOT NULL, seen_at INTEGER NOT NULL, feed TEXT NOT NULL,
                group_hint TEXT, embedding BLOB, event_id TEXT, snippet TEXT);
            CREATE INDEX IF NOT EXISTS ix_articles_seen ON articles (seen_at);
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, title_language TEXT NOT NULL, vector_sum BLOB NOT NULL,
                article_count INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events_last_seen ON events (last_seen);
            CREATE TABLE IF NOT EXISTS feed_state (feed TEXT PRIMARY KEY, last_fetched INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS summaries (
                event_id TEXT NOT NULL, version INTEGER NOT NULL, headline TEXT, summary TEXT NOT NULL, model TEXT NOT NULL,
                article_count INTEGER NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (event_id, version));
            CREATE TABLE IF NOT EXISTS situations (id TEXT PRIMARY KEY, vector_sum BLOB NOT NULL, last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS situation_watches (
                username TEXT NOT NULL, situation_id TEXT NOT NULL, event_count INTEGER NOT NULL, created_at INTEGER NOT NULL,
                PRIMARY KEY (username, situation_id));
            CREATE TABLE IF NOT EXISTS world_news_alerts (
                id TEXT PRIMARY KEY, username TEXT NOT NULL, situation_id TEXT NOT NULL, title TEXT NOT NULL,
                message TEXT NOT NULL, created_at INTEGER NOT NULL, read_at INTEGER);
            CREATE INDEX IF NOT EXISTS ix_world_news_alerts_user ON world_news_alerts (username, read_at, created_at);
            CREATE TABLE IF NOT EXISTS situation_placements (
                situation_id TEXT PRIMARY KEY, country_code TEXT, city TEXT, hidden INTEGER NOT NULL,
                updated_by TEXT NOT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS surge_watchers (username TEXT PRIMARY KEY, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS surge_alerts_sent (situation_id TEXT PRIMARY KEY, sent_at INTEGER NOT NULL);
            """);
        // Databases from before outlet feeds (21-10-26) have no snippet column.
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('articles') WHERE name = 'snippet'";
        if ((long)columns.ExecuteScalar()! == 0) Execute(connection, "ALTER TABLE articles ADD COLUMN snippet TEXT");
        // Nor do databases from before situations (28-10-26) have events.situation_id.
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('events') WHERE name = 'situation_id'";
        if ((long)columns.ExecuteScalar()! == 0) Execute(connection, "ALTER TABLE events ADD COLUMN situation_id TEXT");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_events_situation ON events (situation_id)");
        // Nor situations.kind, from before Overwatch's kinds (29-10-26).
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('situations') WHERE name = 'kind'";
        if ((long)columns.ExecuteScalar()! == 0) Execute(connection, "ALTER TABLE situations ADD COLUMN kind TEXT");
        // A new or small database gets the covering index straight away; building it over a big one
        // takes minutes, so that's EnsureCoveringIndex's job, off the startup path.
        columns.CommandText = $"SELECT COUNT(*) FROM (SELECT 1 FROM articles LIMIT {SmallArticleTable + 1})";
        if (!HasCoveringIndex(connection) && (long)columns.ExecuteScalar()! <= SmallArticleTable) CreateCoveringIndex(connection);
    }

    // The event lists read each report's outlet, country and language. With an index on event_id
    // alone that meant fetching every report's row, about 4.5 KB with its embedding: the 24-hour
    // list read ~180,000 rows of a 2 GB table, 50-70 s on the droplet (30-10-26). This index holds
    // those columns, so the lists read it instead. It replaces ix_articles_event (its first column).
    private const string CoveringIndex = "ix_articles_event_cover";
    private const int SmallArticleTable = 10_000;

    // Builds the covering index on a database that was too big to build it at open. True if it did.
    public bool EnsureCoveringIndex()
    {
        using var connection = Open();
        if (HasCoveringIndex(connection)) return false;
        CreateCoveringIndex(connection);
        return true;
    }

    private static bool HasCoveringIndex(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", CoveringIndex);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static void CreateCoveringIndex(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // The timeout only counts waiting on other writers; the build itself may take minutes.
        command.CommandTimeout = 0;
        command.CommandText = $"""
            CREATE INDEX IF NOT EXISTS {CoveringIndex} ON articles (event_id, outlet, country, language, seen_at);
            DROP INDEX IF EXISTS ix_articles_event;
            """;
        command.ExecuteNonQuery();
    }

    // New reports only (a report already stored, by canonical URL, is skipped). Returns how many were new.
    public int AddArticles(IEnumerable<WorldArticle> articles)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO articles (id, url, title, outlet, language, country, seen_at, feed, group_hint, snippet)
            VALUES ($id, $url, $title, $outlet, $language, $country, $seen, $feed, $hint, $snippet)
            """;
        var parameters = new[] { "$id", "$url", "$title", "$outlet", "$language", "$country", "$seen", "$feed", "$hint", "$snippet" }
            .Select(name => command.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        var added = 0;
        foreach (var a in articles)
        {
            object?[] values = [a.Id, a.Url, a.Title, a.Outlet, a.Language, a.Country, a.SeenAt.ToUnixTimeSeconds(), a.Feed, a.GroupHint, a.Snippet];
            for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
            added += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public IReadOnlyList<WorldArticle> ArticlesToEmbed(int limit) =>
        QueryArticles("WHERE embedding IS NULL ORDER BY seen_at LIMIT $limit", ("$limit", limit));

    // Newest first, and none older than since: a report too old to join a live event isn't worth
    // the CPU, and on a busy server the oldest-first order spent every read catching up on stale news.
    public IReadOnlyList<WorldArticle> RecentArticlesToEmbed(int limit, DateTimeOffset since) =>
        QueryArticles("WHERE embedding IS NULL AND seen_at >= $since ORDER BY seen_at DESC LIMIT $limit",
            ("$limit", limit), ("$since", since.ToUnixTimeSeconds()));

    public void SaveEmbeddings(IReadOnlyList<(string Id, float[] Vector)> embeddings)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET embedding = $vector WHERE id = $id";
        var id = command.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
        var vector = command.Parameters.Add(new SqliteParameter { ParameterName = "$vector" });
        foreach (var (articleId, values) in embeddings)
        {
            id.Value = articleId;
            vector.Value = ToBytes(values);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<(WorldArticle Article, float[] Vector)> EmbeddedUnassigned()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ArticleColumns}, embedding FROM articles WHERE embedding IS NOT NULL AND event_id IS NULL ORDER BY seen_at";
        using var reader = command.ExecuteReader();
        var items = new List<(WorldArticle, float[])>();
        while (reader.Read()) items.Add((ReadArticle(reader), ToFloats((byte[])reader[10])));
        return items;
    }

    public List<NewsEvent> ActiveEvents(DateTimeOffset since) =>
        ReadEvents("WHERE last_seen >= $since", ("$since", since.ToUnixTimeSeconds()));

    private List<NewsEvent> ReadEvents(string where, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, title, title_language, vector_sum, article_count, first_seen, last_seen FROM events {where}";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var events = new List<NewsEvent>();
        while (reader.Read())
        {
            events.Add(new NewsEvent
            {
                Id = reader.GetString(0), Title = reader.GetString(1), TitleLanguage = reader.GetString(2),
                Sum = ToFloats((byte[])reader[3]), ArticleCount = reader.GetInt32(4),
                FirstSeen = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
                LastSeen = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6))
            });
        }
        return events;
    }

    // Events big enough to place in a situation that haven't been, oldest first.
    public List<NewsEvent> EventsToLink(DateTimeOffset since, int minArticles) =>
        ReadEvents("WHERE situation_id IS NULL AND article_count >= $min AND last_seen >= $since ORDER BY first_seen",
            ("$min", minArticles), ("$since", since.ToUnixTimeSeconds()));

    public List<NewsSituation> ActiveSituations(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, vector_sum, last_seen FROM situations WHERE last_seen >= $since";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var situations = new List<NewsSituation>();
        while (reader.Read())
        {
            situations.Add(new NewsSituation
            {
                Id = reader.GetString(0), Sum = ToFloats((byte[])reader[1]),
                LastSeen = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))
            });
        }
        return situations;
    }

    public void SaveLinks(IReadOnlyList<SituationLink> links)
    {
        if (links.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var situation in links.Select(link => link.Situation).Distinct())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO situations (id, vector_sum, last_seen) VALUES ($id, $sum, $last)
                ON CONFLICT (id) DO UPDATE SET vector_sum = excluded.vector_sum, last_seen = excluded.last_seen
                """;
            command.Parameters.AddWithValue("$id", situation.Id);
            command.Parameters.AddWithValue("$sum", ToBytes(situation.Sum));
            command.Parameters.AddWithValue("$last", situation.LastSeen.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        foreach (var link in links)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET situation_id = $situation WHERE id = $event";
            command.Parameters.AddWithValue("$situation", link.Situation.Id);
            command.Parameters.AddWithValue("$event", link.EventId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // Which event each recent feed group went to, so later reports in that group follow it.
    public Dictionary<string, string> GroupHintEvents(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_hint, MIN(event_id) FROM articles
            WHERE group_hint IS NOT NULL AND event_id IS NOT NULL AND seen_at >= $since GROUP BY group_hint
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var hints = new Dictionary<string, string>();
        while (reader.Read()) hints[reader.GetString(0)] = reader.GetString(1);
        return hints;
    }

    public void SaveClustering(ClusterResult result)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO events (id, title, title_language, vector_sum, article_count, first_seen, last_seen)
                VALUES ($id, $title, $language, $sum, $count, $first, $last)
                ON CONFLICT(id) DO UPDATE SET title = excluded.title, title_language = excluded.title_language,
                    vector_sum = excluded.vector_sum, article_count = excluded.article_count,
                    first_seen = excluded.first_seen, last_seen = excluded.last_seen
                """;
            foreach (var e in result.Touched)
            {
                upsert.Parameters.Clear();
                upsert.Parameters.AddWithValue("$id", e.Id);
                upsert.Parameters.AddWithValue("$title", e.Title);
                upsert.Parameters.AddWithValue("$language", e.TitleLanguage);
                upsert.Parameters.AddWithValue("$sum", ToBytes(e.Sum));
                upsert.Parameters.AddWithValue("$count", e.ArticleCount);
                upsert.Parameters.AddWithValue("$first", e.FirstSeen.ToUnixTimeSeconds());
                upsert.Parameters.AddWithValue("$last", e.LastSeen.ToUnixTimeSeconds());
                upsert.ExecuteNonQuery();
            }
        }
        using (var assign = connection.CreateCommand())
        {
            assign.CommandText = "UPDATE articles SET event_id = $event WHERE id = $id";
            var id = assign.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
            var eventId = assign.Parameters.Add(new SqliteParameter { ParameterName = "$event" });
            foreach (var (articleId, target) in result.Assignments)
            {
                id.Value = articleId;
                eventId.Value = target;
                assign.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    public void ApplyMerges(IReadOnlyList<EventMerge> merges)
    {
        if (merges.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var merge in merges)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE articles SET event_id = $keep WHERE event_id = $drop;
                UPDATE events SET situation_id = COALESCE(situation_id, (SELECT situation_id FROM events WHERE id = $drop)) WHERE id = $keep;
                DELETE FROM events WHERE id = $drop;
                DELETE FROM summaries WHERE event_id = $drop;
                UPDATE events SET title = $title, title_language = $language, vector_sum = $sum, article_count = $count,
                    first_seen = $first, last_seen = $last WHERE id = $keep;
                """;
            command.Parameters.AddWithValue("$keep", merge.Keep.Id);
            command.Parameters.AddWithValue("$drop", merge.Drop.Id);
            command.Parameters.AddWithValue("$title", merge.Keep.Title);
            command.Parameters.AddWithValue("$language", merge.Keep.TitleLanguage);
            command.Parameters.AddWithValue("$sum", ToBytes(merge.Keep.Sum));
            command.Parameters.AddWithValue("$count", merge.Keep.ArticleCount);
            command.Parameters.AddWithValue("$first", merge.Keep.FirstSeen.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$last", merge.Keep.LastSeen.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // The busiest events since a time, by distinct outlets.
    public IReadOnlyList<EventOverview> TopEvents(DateTimeOffset since, int limit) =>
        Overviews("WHERE e.last_seen >= $since", "", ("$since", since.ToUnixTimeSeconds()), ("$limit", limit));

    // Situations of two or more events with news since a time, busiest (by distinct outlets) first.
    public IReadOnlyList<SituationOverview> TopSituations(DateTimeOffset since, int limit) => Situations(since, limit, onlyId: null);

    // One situation straight from its id (a link, a panel), rather than ranking every one.
    public SituationOverview? Situation(string situationId) =>
        Situations(DateTimeOffset.UnixEpoch, 1, onlyId: situationId).FirstOrDefault();

    private IReadOnlyList<SituationOverview> Situations(DateTimeOffset since, int limit, string? onlyId)
    {
        using var connection = Open();
        var rows = new List<(string Id, int Events, int Articles, int Outlets, string? Countries, string? Languages, long First, long Last, string? Kind)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.situation_id, COUNT(DISTINCT e.id), COUNT(*), COUNT(DISTINCT a.outlet),
                       GROUP_CONCAT(DISTINCT a.country), GROUP_CONCAT(DISTINCT a.language), MIN(e.first_seen), MAX(e.last_seen),
                       (SELECT kind FROM situations s WHERE s.id = e.situation_id)
                FROM events e JOIN articles a ON a.event_id = e.id
                WHERE e.situation_id IN (SELECT situation_id FROM events WHERE situation_id IS NOT NULL
                                         AND ($only IS NULL OR situation_id = $only)
                                         GROUP BY situation_id HAVING COUNT(*) >= 2 AND MAX(last_seen) >= $since)
                GROUP BY e.situation_id
                ORDER BY COUNT(DISTINCT a.outlet) DESC, MAX(e.last_seen) DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$only", (object?)onlyId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        if (rows.Count == 0) return [];

        // Each situation's events, for its title (the biggest, English when there is one) and latest.
        var events = new Dictionary<string, List<(string Title, bool English, int Articles, long Last)>>();
        using (var command = connection.CreateCommand())
        {
            var names = rows.Select((_, i) => $"$s{i}").ToList();
            command.CommandText = $"""
                SELECT situation_id, COALESCE((SELECT headline FROM summaries WHERE event_id = e.id AND headline IS NOT NULL
                       ORDER BY version DESC LIMIT 1), title), title_language, article_count, last_seen
                FROM events e WHERE situation_id IN ({string.Join(", ", names)})
                """;
            for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue(names[i], rows[i].Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!events.TryGetValue(id, out var list)) events[id] = list = [];
                list.Add((reader.GetString(1), reader.GetString(2) == "English", reader.GetInt32(3), reader.GetInt64(4)));
            }
        }

        return rows.Select(row =>
        {
            var list = events.GetValueOrDefault(row.Id) ?? [];
            var title = list.OrderByDescending(e => e.English).ThenByDescending(e => e.Articles).Select(e => e.Title).FirstOrDefault() ?? "";
            var latest = list.OrderByDescending(e => e.Last).Select(e => e.Title).FirstOrDefault() ?? title;
            return new SituationOverview(row.Id, title, latest, row.Events, row.Articles, row.Outlets, Split(row.Countries),
                Split(row.Languages), DateTimeOffset.FromUnixTimeSeconds(row.First), DateTimeOffset.FromUnixTimeSeconds(row.Last), row.Kind);
        }).ToList();
    }


    // Recent events worth searching, with their meaning vectors and situations.
    public IReadOnlyList<WorldNewsCandidate> SearchCandidates(DateTimeOffset since, int minArticles)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, vector_sum, situation_id FROM events WHERE last_seen >= $since AND article_count >= $min";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$min", minArticles);
        using var reader = command.ExecuteReader();
        var candidates = new List<WorldNewsCandidate>();
        while (reader.Read())
            candidates.Add(new WorldNewsCandidate(reader.GetString(0), ToFloats((byte[])reader[1]), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return candidates;
    }

    // A situation's events in the order they broke.
    public IReadOnlyList<EventOverview> EventsForSituation(string situationId) =>
        Overviews("WHERE e.situation_id = $situation", "", ("$situation", situationId), ("$limit", 500))
            .OrderBy(e => e.FirstSeen).ToList();

    public bool IsWatching(string username, string situationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM situation_watches WHERE username = $username AND situation_id = $situation";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$situation", situationId);
        return (long)command.ExecuteScalar()! > 0;
    }

    public void Watch(string username, string situationId, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO situation_watches (username, situation_id, event_count, created_at)
            SELECT $username, $situation, COUNT(*), $created FROM events WHERE situation_id = $situation
            """;
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$situation", situationId);
        command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void Unwatch(string username, string situationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM situation_watches WHERE username = $username AND situation_id = $situation";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$situation", situationId);
        command.ExecuteNonQuery();
    }

    // Every correction (a short list: one per situation someone fixed by hand).
    public IReadOnlyDictionary<string, SituationPlacement> Placements()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT situation_id, country_code, city, hidden, updated_by, updated_at FROM situation_placements";
        using var reader = command.ExecuteReader();
        var placements = new Dictionary<string, SituationPlacement>(StringComparer.Ordinal);
        while (reader.Read())
            placements[reader.GetString(0)] = new SituationPlacement(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3) != 0, reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)));
        return placements;
    }

    public void SetPlacement(SituationPlacement placement)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO situation_placements (situation_id, country_code, city, hidden, updated_by, updated_at)
            VALUES ($situation, $country, $city, $hidden, $by, $at)
            """;
        command.Parameters.AddWithValue("$situation", placement.SituationId);
        command.Parameters.AddWithValue("$country", (object?)placement.CountryCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$city", (object?)placement.City ?? DBNull.Value);
        command.Parameters.AddWithValue("$hidden", placement.Hidden ? 1 : 0);
        command.Parameters.AddWithValue("$by", placement.UpdatedBy);
        command.Parameters.AddWithValue("$at", placement.UpdatedAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void ClearPlacement(string situationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM situation_placements WHERE situation_id = $situation";
        command.Parameters.AddWithValue("$situation", situationId);
        command.ExecuteNonQuery();
    }

    // What each situation is about (SituationKinds). onlyMissing keeps kinds a better classifier
    // already set, for when the keyword fallback stands in.
    public void SaveKinds(IReadOnlyList<(string SituationId, string Kind)> kinds, bool onlyMissing = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = onlyMissing
            ? "UPDATE situations SET kind = $kind WHERE id = $id AND kind IS NULL"
            : "UPDATE situations SET kind = $kind WHERE id = $id";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        foreach (var (situationId, situationKind) in kinds)
        {
            id.Value = situationId;
            kind.Value = situationKind;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // How many distinct outlets in each edition country covered a situation, most first.
    public IReadOnlyList<(string Country, int Outlets)> SituationCoverage(string situationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.country, COUNT(DISTINCT a.outlet) FROM events e JOIN articles a ON a.event_id = e.id
            WHERE e.situation_id = $situation GROUP BY a.country ORDER BY 2 DESC
            """;
        command.Parameters.AddWithValue("$situation", situationId);
        using var reader = command.ExecuteReader();
        var coverage = new List<(string, int)>();
        while (reader.Read()) coverage.Add((reader.GetString(0), reader.GetInt32(1)));
        return coverage;
    }

    // Distinct outlets on each situation with news since cut: all of them, and those that had
    // reported before cut. A story whose coverage doubles in a couple of hours is surging.
    public IReadOnlyDictionary<string, (int Outlets, int OutletsBefore)> SituationGrowth(DateTimeOffset cut)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.situation_id, COUNT(DISTINCT a.outlet), COUNT(DISTINCT CASE WHEN a.seen_at < $cut THEN a.outlet END)
            FROM events e JOIN articles a ON a.event_id = e.id
            WHERE e.situation_id IN (SELECT situation_id FROM events WHERE situation_id IS NOT NULL AND last_seen >= $cut)
            GROUP BY e.situation_id
            """;
        command.Parameters.AddWithValue("$cut", cut.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var growth = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        while (reader.Read()) growth[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        return growth;
    }

    // Called after a read has linked new events. One alert covers all events added since the last read.
    public IReadOnlyList<WorldNewsAlert> CreateWatchAlerts(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var pending = new List<(string Username, string SituationId, int Before, int Current, string Title)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT w.username, w.situation_id, w.event_count, COUNT(e.id),
                       COALESCE((SELECT e2.title FROM events e2 WHERE e2.situation_id = w.situation_id ORDER BY e2.last_seen DESC LIMIT 1), 'Developing story')
                FROM situation_watches w LEFT JOIN events e ON e.situation_id = w.situation_id
                GROUP BY w.username, w.situation_id, w.event_count HAVING COUNT(e.id) > w.event_count
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) pending.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
        }
        var alerts = new List<WorldNewsAlert>();
        foreach (var item in pending)
        {
            var alert = new WorldNewsAlert(Guid.NewGuid(), item.Username, item.SituationId, item.Title,
                $"{item.Current - item.Before:N0} new {(item.Current - item.Before == 1 ? "event" : "events")} in this watched situation.", now, false);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO world_news_alerts (id, username, situation_id, title, message, created_at)
                VALUES ($id, $username, $situation, $title, $message, $created);
                UPDATE situation_watches SET event_count = $count WHERE username = $username AND situation_id = $situation;
                """;
            insert.Parameters.AddWithValue("$id", alert.Id.ToString("N"));
            insert.Parameters.AddWithValue("$username", item.Username);
            insert.Parameters.AddWithValue("$situation", item.SituationId);
            insert.Parameters.AddWithValue("$title", alert.Title);
            insert.Parameters.AddWithValue("$message", alert.Message);
            insert.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$count", item.Current);
            insert.ExecuteNonQuery();
            alerts.Add(alert);
        }
        transaction.Commit();
        return alerts;
    }

    public bool IsWatchingSurges(string username)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM surge_watchers WHERE username = $username";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        return (long)command.ExecuteScalar()! > 0;
    }

    public void WatchSurges(string username, bool on, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = on
            ? "INSERT OR IGNORE INTO surge_watchers (username, created_at) VALUES ($username, $created)"
            : "DELETE FROM surge_watchers WHERE username = $username";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    // One alert per surging situation for everyone who asked, at most once a day per situation.
    public IReadOnlyList<WorldNewsAlert> CreateSurgeAlerts(IReadOnlyList<(string SituationId, string Title, string Message)> surging,
        DateTimeOffset now)
    {
        if (surging.Count == 0) return [];
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var watchers = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT username FROM surge_watchers";
            using var reader = command.ExecuteReader();
            while (reader.Read()) watchers.Add(reader.GetString(0));
        }
        var alerts = new List<WorldNewsAlert>();
        foreach (var (situationId, title, message) in surging)
        {
            using var claim = connection.CreateCommand();
            claim.CommandText = """
                INSERT INTO surge_alerts_sent (situation_id, sent_at) VALUES ($situation, $now)
                ON CONFLICT (situation_id) DO UPDATE SET sent_at = $now WHERE sent_at < $dayAgo
                """;
            claim.Parameters.AddWithValue("$situation", situationId);
            claim.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            claim.Parameters.AddWithValue("$dayAgo", now.AddDays(-1).ToUnixTimeSeconds());
            if (claim.ExecuteNonQuery() == 0) continue;
            foreach (var username in watchers)
            {
                var alert = new WorldNewsAlert(Guid.NewGuid(), username, situationId, title, message, now, false);
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO world_news_alerts (id, username, situation_id, title, message, created_at)
                    VALUES ($id, $username, $situation, $title, $message, $created)
                    """;
                insert.Parameters.AddWithValue("$id", alert.Id.ToString("N"));
                insert.Parameters.AddWithValue("$username", username);
                insert.Parameters.AddWithValue("$situation", situationId);
                insert.Parameters.AddWithValue("$title", title);
                insert.Parameters.AddWithValue("$message", message);
                insert.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
                insert.ExecuteNonQuery();
                alerts.Add(alert);
            }
        }
        transaction.Commit();
        return alerts;
    }

    public IReadOnlyList<WorldNewsAlert> Alerts(string username)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, situation_id, title, message, created_at, read_at FROM world_news_alerts WHERE username = $username ORDER BY created_at DESC LIMIT 100";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        using var reader = command.ExecuteReader();
        var alerts = new List<WorldNewsAlert>();
        while (reader.Read()) alerts.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), !reader.IsDBNull(6)));
        return alerts;
    }

    public void MarkAlertRead(string username, Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE world_news_alerts SET read_at = $read WHERE id = $id AND username = $username";
        command.Parameters.AddWithValue("$read", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        command.ExecuteNonQuery();
    }

    public EventOverview? Event(string eventId) =>
        Overviews("WHERE e.id = $event", "", ("$event", eventId), ("$limit", 1)).FirstOrDefault();

    // The World view counts reports seen inside the requested window. A story keeps its global
    // event metadata, while RecentReports/RecentOutlets explain why it ranks in this window.
    public WorldSnapshot World(DateTimeOffset since, int storiesPerCountry = 3, int biggestStories = 10)
    {
        using var connection = Open();
        var countryRows = new List<(string Country, int Reports, int Events, int Outlets)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT country, COUNT(*), COUNT(DISTINCT event_id), COUNT(DISTINCT outlet)
                FROM articles WHERE event_id IS NOT NULL AND seen_at >= $since AND TRIM(country) <> ''
                GROUP BY country ORDER BY COUNT(*) DESC, country
                """;
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            while (reader.Read()) countryRows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        // Every country from one pass over the recent reports. It used to be a query per country,
        // each joining an event's recent reports to all of its reports (thousands by thousands for
        // a big story).
        var events = ReadWorldEvents(connection, since);
        var recent = ReadRecentReports(connection, since).Where(report => events.ContainsKey(report.EventId)).ToList();
        WorldStory Story(string eventId, IReadOnlyCollection<(string EventId, string Country, string Outlet, int Reports)> reports)
        {
            var (overview, situationId) = events[eventId];
            var outlets = reports.Select(report => report.Outlet).Distinct().Count();
            return new WorldStory(overview with { OutletCount = outlets }, reports.Sum(report => report.Reports), outlets, situationId);
        }

        IEnumerable<WorldStory> Ranked(IEnumerable<WorldStory> stories) => stories
            .OrderByDescending(story => story.RecentOutlets).ThenByDescending(story => story.RecentReports)
            .ThenByDescending(story => story.Event.LastSeen);

        var countries = countryRows.Select(row => new WorldCountry(row.Country, row.Reports, row.Events, row.Outlets,
            Ranked(recent.Where(report => report.Country == row.Country).GroupBy(report => report.EventId)
                    .Select(group => Story(group.Key, group.ToList())))
                .Take(storiesPerCountry).ToList())).ToList();
        var biggest = Ranked(recent.GroupBy(report => report.EventId).Select(group => Story(group.Key, group.ToList())))
            .Take(biggestStories).ToList();
        return new WorldSnapshot(since, countries, biggest);
    }

    // Events with reports since a time (an event's last_seen is its newest report's), with every
    // country and language they've ever been reported in.
    private static Dictionary<string, (EventOverview Overview, string? SituationId)> ReadWorldEvents(SqliteConnection connection, DateTimeOffset since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH chosen AS MATERIALIZED (SELECT id, title, article_count, first_seen, last_seen, situation_id FROM events WHERE last_seen >= $since)
            SELECT e.id, e.title, e.article_count, GROUP_CONCAT(DISTINCT a.country), GROUP_CONCAT(DISTINCT a.language),
                   e.first_seen, e.last_seen,
                   (SELECT headline FROM summaries WHERE event_id = e.id AND headline IS NOT NULL ORDER BY version DESC LIMIT 1),
                   e.situation_id
            FROM chosen e JOIN articles a ON a.event_id = e.id
            GROUP BY e.id
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var events = new Dictionary<string, (EventOverview, string?)>();
        while (reader.Read())
        {
            events[reader.GetString(0)] = (new EventOverview(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), 0,
                Split(reader.IsDBNull(3) ? null : reader.GetString(3)), Split(reader.IsDBNull(4) ? null : reader.GetString(4)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8));
        }
        return events;
    }

    // Reports since a time, counted by event, country and outlet. CROSS JOIN keeps SQLite starting
    // from the recent events; left to choose, it scanned every report's index entry for seen_at.
    private static IReadOnlyList<(string EventId, string Country, string Outlet, int Reports)> ReadRecentReports(
        SqliteConnection connection, DateTimeOffset since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH chosen AS MATERIALIZED (SELECT id FROM events WHERE last_seen >= $since)
            SELECT a.event_id, a.country, a.outlet, COUNT(*)
            FROM chosen e CROSS JOIN articles a ON a.event_id = e.id
            WHERE a.seen_at >= $since
            GROUP BY a.event_id, a.country, a.outlet
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var reports = new List<(string, string, string, int)>();
        while (reader.Read()) reports.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return reports;
    }

    // The busiest events since a time, covered by at least minOutlets outlets, that have no summary
    // yet or have grown by a quarter (and at least 3 reports) since their latest one.
    public IReadOnlyList<EventOverview> EventsDueSummary(DateTimeOffset since, int minOutlets, int limit) =>
        Overviews("WHERE e.last_seen >= $since", """
            HAVING COUNT(DISTINCT a.outlet) >= $min AND COALESCE(e.article_count >= (
                SELECT article_count + MAX(3, article_count / 4) FROM summaries
                WHERE event_id = e.id ORDER BY version DESC LIMIT 1), 1)
            """, ("$since", since.ToUnixTimeSeconds()), ("$min", minOutlets), ("$limit", limit));

    public EventSummary? LatestSummary(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, version, headline, summary, model, article_count, created_at FROM summaries
            WHERE event_id = $event ORDER BY version DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EventSummary(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)))
            : null;
    }

    // Stores the next version of an event's summary (earlier versions are kept) and returns it.
    public EventSummary SaveSummary(string eventId, string? headline, string text, string model, int articleCount, DateTimeOffset createdAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO summaries (event_id, version, headline, summary, model, article_count, created_at)
            VALUES ($event, (SELECT COALESCE(MAX(version), 0) + 1 FROM summaries WHERE event_id = $event),
                    $headline, $summary, $model, $count, $created)
            RETURNING version
            """;
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$headline", (object?)headline ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", text);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$count", articleCount);
        command.Parameters.AddWithValue("$created", createdAt.ToUnixTimeSeconds());
        var version = Convert.ToInt32(command.ExecuteScalar());
        return new EventSummary(eventId, version, headline, text, model, articleCount, DateTimeOffset.FromUnixTimeSeconds(createdAt.ToUnixTimeSeconds()));
    }

    public IReadOnlyList<WorldArticle> ArticlesForEvent(string eventId) =>
        QueryArticles("WHERE event_id = $event ORDER BY seen_at", ("$event", eventId));

    public DateTimeOffset? LastFetched(string feed)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_fetched FROM feed_state WHERE feed = $feed";
        command.Parameters.AddWithValue("$feed", feed);
        return command.ExecuteScalar() is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    public void SetLastFetched(string feed, DateTimeOffset when)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO feed_state (feed, last_fetched) VALUES ($feed, $when)
            ON CONFLICT(feed) DO UPDATE SET last_fetched = excluded.last_fetched
            """;
        command.Parameters.AddWithValue("$feed", feed);
        command.Parameters.AddWithValue("$when", when.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    // Reports and events older than the cutoff go; the database only holds what can still matter.
    public void Prune(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM articles WHERE seen_at < $before;
            DELETE FROM events WHERE last_seen < $before;
            DELETE FROM summaries WHERE event_id NOT IN (SELECT id FROM events);
            DELETE FROM situations WHERE id NOT IN (SELECT situation_id FROM events WHERE situation_id IS NOT NULL);
            DELETE FROM situation_placements WHERE situation_id NOT IN (SELECT id FROM situations);
            DELETE FROM surge_alerts_sent WHERE situation_id NOT IN (SELECT id FROM situations);
            """;
        command.Parameters.AddWithValue("$before", before.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private const string ArticleColumns = "id, url, title, outlet, language, country, seen_at, feed, group_hint, snippet";

    private IReadOnlyList<EventOverview> Overviews(string where, string having, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // The events are picked first, so `where` uses their index (last_seen, situation_id). Joined
        // straight to articles, SQLite walked every event by id to group them.
        command.CommandText = $"""
            WITH chosen AS MATERIALIZED (SELECT e.id, e.title, e.article_count, e.first_seen, e.last_seen FROM events e {where})
            SELECT e.id, e.title, e.article_count, COUNT(DISTINCT a.outlet),
                   GROUP_CONCAT(DISTINCT a.country), GROUP_CONCAT(DISTINCT a.language), e.first_seen, e.last_seen,
                   (SELECT headline FROM summaries WHERE event_id = e.id AND headline IS NOT NULL ORDER BY version DESC LIMIT 1)
            FROM chosen e JOIN articles a ON a.event_id = e.id
            GROUP BY e.id {having}
            ORDER BY COUNT(DISTINCT a.outlet) DESC, e.last_seen DESC LIMIT $limit
            """;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var events = new List<EventOverview>();
        while (reader.Read())
        {
            events.Add(new EventOverview(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                Split(reader.IsDBNull(4) ? null : reader.GetString(4)), Split(reader.IsDBNull(5) ? null : reader.GetString(5)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return events;
    }

    private IReadOnlyList<WorldArticle> QueryArticles(string where, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ArticleColumns} FROM articles {where}";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var articles = new List<WorldArticle>();
        while (reader.Read()) articles.Add(ReadArticle(reader));
        return articles;
    }

    private static WorldArticle ReadArticle(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9));

    private static IReadOnlyList<string> Split(string? values) =>
        values is null ? [] : values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] ToBytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static float[] ToFloats(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}
