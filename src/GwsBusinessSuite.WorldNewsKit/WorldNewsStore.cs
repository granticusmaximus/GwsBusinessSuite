using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace GwsBusinessSuite.WorldNewsKit;

// An event as the feed shows it: its reports counted by distinct outlet, country and language.
public sealed record EventOverview(string Id, string Title, int ArticleCount, int OutletCount,
    IReadOnlyList<string> Countries, IReadOnlyList<string> Languages, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

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
                group_hint TEXT, embedding BLOB, event_id TEXT);
            CREATE INDEX IF NOT EXISTS ix_articles_seen ON articles (seen_at);
            CREATE INDEX IF NOT EXISTS ix_articles_event ON articles (event_id);
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, title_language TEXT NOT NULL, vector_sum BLOB NOT NULL,
                article_count INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events_last_seen ON events (last_seen);
            CREATE TABLE IF NOT EXISTS feed_state (feed TEXT PRIMARY KEY, last_fetched INTEGER NOT NULL);
            """);
    }

    // New reports only (a report already stored, by canonical URL, is skipped). Returns how many were new.
    public int AddArticles(IEnumerable<WorldArticle> articles)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO articles (id, url, title, outlet, language, country, seen_at, feed, group_hint)
            VALUES ($id, $url, $title, $outlet, $language, $country, $seen, $feed, $hint)
            """;
        var parameters = new[] { "$id", "$url", "$title", "$outlet", "$language", "$country", "$seen", "$feed", "$hint" }
            .Select(name => command.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        var added = 0;
        foreach (var a in articles)
        {
            object?[] values = [a.Id, a.Url, a.Title, a.Outlet, a.Language, a.Country, a.SeenAt.ToUnixTimeSeconds(), a.Feed, a.GroupHint];
            for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
            added += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public IReadOnlyList<WorldArticle> ArticlesToEmbed(int limit) =>
        QueryArticles("WHERE embedding IS NULL ORDER BY seen_at LIMIT $limit", ("$limit", limit));

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
        while (reader.Read()) items.Add((ReadArticle(reader), ToFloats((byte[])reader[9])));
        return items;
    }

    public List<NewsEvent> ActiveEvents(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, title_language, vector_sum, article_count, first_seen, last_seen
            FROM events WHERE last_seen >= $since
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
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
                DELETE FROM events WHERE id = $drop;
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
    public IReadOnlyList<EventOverview> TopEvents(DateTimeOffset since, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.title, e.article_count, COUNT(DISTINCT a.outlet),
                   GROUP_CONCAT(DISTINCT a.country), GROUP_CONCAT(DISTINCT a.language), e.first_seen, e.last_seen
            FROM events e JOIN articles a ON a.event_id = e.id
            WHERE e.last_seen >= $since
            GROUP BY e.id ORDER BY COUNT(DISTINCT a.outlet) DESC, e.last_seen DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var events = new List<EventOverview>();
        while (reader.Read())
        {
            events.Add(new EventOverview(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                Split(reader.IsDBNull(4) ? null : reader.GetString(4)), Split(reader.IsDBNull(5) ? null : reader.GetString(5)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7))));
        }
        return events;
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
            """;
        command.Parameters.AddWithValue("$before", before.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private const string ArticleColumns = "id, url, title, outlet, language, country, seen_at, feed, group_hint";

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
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));

    private static IReadOnlyList<string> Split(string? values) =>
        values is null ? [] : values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
