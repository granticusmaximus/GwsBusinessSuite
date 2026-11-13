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
// the event had when it was written, which decides when it's due again. Comparison is how the
// coverage compares, one point per line: empty when the model gave none, null for a summary from
// before comparisons (06-11-26).
public sealed record EventSummary(string EventId, int Version, string? Headline, string Text, string Model,
    int ArticleCount, DateTimeOffset CreatedAt, IReadOnlyList<string>? Comparison = null);

public sealed record WorldNewsAlert(Guid Id, string Username, string SituationId, string Title, string Message,
    DateTimeOffset CreatedAt, bool IsRead);

// A hand correction of where Overwatch puts a situation: a country code (and optional city), or
// Hidden to keep it off the globe.
public sealed record SituationPlacement(string SituationId, string? CountryCode, string? City, bool Hidden,
    string UpdatedBy, DateTimeOffset UpdatedAt);
// A watchlist entry (1.11): a search a person follows.
public sealed record KeywordWatch(string Id, string Username, string Query, DateTimeOffset CreatedAt);

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
            CREATE TABLE IF NOT EXISTS ingestion_cursors (source TEXT PRIMARY KEY, last_id INTEGER NOT NULL);
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
            CREATE TABLE IF NOT EXISTS analyses (
                event_id TEXT PRIMARY KEY, claim TEXT NOT NULL, summary TEXT NOT NULL, agreed TEXT NOT NULL, differences TEXT NOT NULL,
                model TEXT NOT NULL, article_count INTEGER NOT NULL, articles_read INTEGER NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS article_texts (article_id TEXT PRIMARY KEY, excerpt TEXT, reason TEXT, fetched_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS analysis_history (
                event_id TEXT NOT NULL, claim TEXT NOT NULL, summary TEXT NOT NULL, agreed TEXT NOT NULL, differences TEXT NOT NULL,
                model TEXT NOT NULL, article_count INTEGER NOT NULL, articles_read INTEGER NOT NULL, created_at INTEGER NOT NULL,
                PRIMARY KEY (event_id, created_at));
            CREATE TABLE IF NOT EXISTS story_reads (
                username TEXT NOT NULL, event_id TEXT NOT NULL, analysis_at INTEGER NOT NULL, read_at INTEGER NOT NULL,
                PRIMARY KEY (username, event_id));
            CREATE TABLE IF NOT EXISTS article_translations (article_id TEXT PRIMARY KEY, english TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS surge_alerts_sent (situation_id TEXT PRIMARY KEY, sent_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS desk_checks (
                event_id TEXT NOT NULL, item TEXT NOT NULL, done INTEGER NOT NULL, evidence TEXT, checked_by TEXT, checked_at INTEGER, PRIMARY KEY (event_id, item));
            CREATE TABLE IF NOT EXISTS budget (
                event_id TEXT PRIMARY KEY, title TEXT NOT NULL, owner TEXT NOT NULL, status TEXT NOT NULL, next_action TEXT NOT NULL,
                updated_at INTEGER NOT NULL, updated_by TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tips (
                id TEXT PRIMARY KEY, name TEXT, contact TEXT, message TEXT NOT NULL, url TEXT, created_at INTEGER NOT NULL, status TEXT NOT NULL,
                event_id TEXT, handled_by TEXT);
            CREATE TABLE IF NOT EXISTS article_body_edits (
                article_id TEXT PRIMARY KEY, first_text TEXT NOT NULL, latest_text TEXT NOT NULL, detected_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS outlet_owners (outlet TEXT NOT NULL, owner TEXT NOT NULL, fetched_at INTEGER NOT NULL, PRIMARY KEY (outlet, owner));
            CREATE TABLE IF NOT EXISTS label_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL, level TEXT NOT NULL, reason TEXT NOT NULL, independent INTEGER NOT NULL,
                outlets INTEGER NOT NULL, rules_version TEXT NOT NULL, taken_at INTEGER NOT NULL, previous_hash TEXT NOT NULL, hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_label_snapshots_event ON label_snapshots (event_id, id);
            CREATE TABLE IF NOT EXISTS label_challenges (
                id TEXT PRIMARY KEY, event_id TEXT NOT NULL, username TEXT NOT NULL, note TEXT NOT NULL, evidence_url TEXT, created_at INTEGER NOT NULL,
                status TEXT NOT NULL, resolved_by TEXT, resolution TEXT, resolved_at INTEGER);
            CREATE TABLE IF NOT EXISTS custom_feeds (
                url TEXT PRIMARY KEY, name TEXT NOT NULL, language TEXT NOT NULL, country TEXT NOT NULL, kind TEXT NOT NULL,
                added_by TEXT NOT NULL, added_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS paused_feeds (url TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS outlet_days (outlet TEXT NOT NULL, day INTEGER NOT NULL, reports INTEGER NOT NULL, PRIMARY KEY (outlet, day));
            CREATE TABLE IF NOT EXISTS country_hours (country TEXT NOT NULL, hour INTEGER NOT NULL, reports INTEGER NOT NULL, PRIMARY KEY (country, hour));
            CREATE TABLE IF NOT EXISTS keyword_watches (
                id TEXT PRIMARY KEY, username TEXT NOT NULL, query TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_keyword_watches_user ON keyword_watches (username);
            CREATE TABLE IF NOT EXISTS keyword_watch_hits (watch_id TEXT NOT NULL, situation_id TEXT NOT NULL, PRIMARY KEY (watch_id, situation_id));
            CREATE TABLE IF NOT EXISTS feed_health (
                url TEXT PRIMARY KEY, etag TEXT, last_modified TEXT, last_status INTEGER, last_attempt INTEGER, last_success INTEGER,
                last_error TEXT, failures INTEGER NOT NULL, empty_reads INTEGER NOT NULL, added INTEGER NOT NULL, skip_until INTEGER);
            CREATE TABLE IF NOT EXISTS article_edits (
                article_id TEXT NOT NULL, field TEXT NOT NULL, before TEXT NOT NULL, after TEXT NOT NULL,
                kind TEXT NOT NULL, seen_at INTEGER NOT NULL, PRIMARY KEY (article_id, field, seen_at));
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
        // Nor summaries.comparison, from before the coverage comparison (06-11-26). NULL there means
        // "written before comparisons", so a current event is asked once more; '' means none was given.
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('summaries') WHERE name = 'comparison'";
        if ((long)columns.ExecuteScalar()! == 0) Execute(connection, "ALTER TABLE summaries ADD COLUMN comparison TEXT");
        // A new or small database gets the covering index straight away; building it over a big one
        // takes minutes, so that's EnsureCoveringIndex's job, off the startup path.
        columns.CommandText = $"SELECT COUNT(*) FROM (SELECT 1 FROM articles LIMIT {SmallArticleTable + 1})";
        if (!HasCoveringIndex(connection) && (long)columns.ExecuteScalar()! <= SmallArticleTable) CreateCoveringIndex(connection);
        // Full-text search over headlines and descriptions (World News trust plan 1.10, 13-11-26).
        // The triggers keep it current from now on; a big database's existing reports are added by
        // EnsureFullTextIndex, off the startup path, and a small one's straight away.
        Execute(connection, FullTextSchema);
        if (!IsFullTextBuilt(connection) && (long)columns.ExecuteScalar()! <= SmallArticleTable) BuildFullText(connection);
    }

    private const string FullTextSchema = """
        CREATE VIRTUAL TABLE IF NOT EXISTS articles_fts USING fts5(title, snippet, content='articles', content_rowid='rowid',
            tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER IF NOT EXISTS articles_fts_insert AFTER INSERT ON articles BEGIN
            INSERT INTO articles_fts(rowid, title, snippet) VALUES (new.rowid, new.title, new.snippet); END;
        CREATE TRIGGER IF NOT EXISTS articles_fts_delete AFTER DELETE ON articles BEGIN
            INSERT INTO articles_fts(articles_fts, rowid, title, snippet) VALUES ('delete', old.rowid, old.title, old.snippet); END;
        CREATE TRIGGER IF NOT EXISTS articles_fts_update AFTER UPDATE OF title, snippet ON articles BEGIN
            INSERT INTO articles_fts(articles_fts, rowid, title, snippet) VALUES ('delete', old.rowid, old.title, old.snippet);
            INSERT INTO articles_fts(rowid, title, snippet) VALUES (new.rowid, new.title, new.snippet); END;
        CREATE TABLE IF NOT EXISTS store_flags (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
        """;

    private static bool IsFullTextBuilt(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM store_flags WHERE name = 'fts_built' AND value = 1";
        return (long)command.ExecuteScalar()! > 0;
    }

    private static void BuildFullText(SqliteConnection connection) =>
        Execute(connection, "INSERT INTO articles_fts(articles_fts) VALUES ('rebuild'); INSERT OR REPLACE INTO store_flags (name, value) VALUES ('fts_built', 1);");

    // Adds the existing reports to the full-text index once (minutes on the droplet's database).
    public bool EnsureFullTextIndex()
    {
        using var connection = Open();
        if (IsFullTextBuilt(connection)) return false;
        BuildFullText(connection);
        return true;
    }

    public bool FullTextReady()
    {
        using var connection = Open();
        return IsFullTextBuilt(connection);
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
    // Press releases and listicles are dropped (NewsNoise, 13-11-26). A report seen again with a
    // different headline keeps its first-seen headline, and the change is recorded in article_edits
    // (corrections and quiet edits, World News trust plan 1.8/1.9).
    public int AddArticles(IEnumerable<WorldArticle> articles)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT title, (SELECT after FROM article_edits WHERE article_id = $id AND field = 'title' ORDER BY seen_at DESC LIMIT 1) FROM articles WHERE id = $id";
        var existingId = existing.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
        using var edit = connection.CreateCommand();
        edit.CommandText = "INSERT OR IGNORE INTO article_edits (article_id, field, before, after, kind, seen_at) VALUES ($id, 'title', $before, $after, $kind, $seen)";
        var editParameters = new[] { "$id", "$before", "$after", "$kind", "$seen" }
            .Select(name => edit.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        using var hours = connection.CreateCommand();
        hours.CommandText = "INSERT INTO country_hours (country, hour, reports) VALUES ($country, $hour, 1) ON CONFLICT(country, hour) DO UPDATE SET reports = reports + 1";
        var hourCountry = hours.Parameters.Add(new SqliteParameter { ParameterName = "$country" });
        var hourStart = hours.Parameters.Add(new SqliteParameter { ParameterName = "$hour" });
        using var days = connection.CreateCommand();
        days.CommandText = "INSERT INTO outlet_days (outlet, day, reports) VALUES ($outlet, $day, 1) ON CONFLICT(outlet, day) DO UPDATE SET reports = reports + 1";
        var dayOutlet = days.Parameters.Add(new SqliteParameter { ParameterName = "$outlet" });
        var dayStart = days.Parameters.Add(new SqliteParameter { ParameterName = "$day" });
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO articles (id, url, title, outlet, language, country, seen_at, feed, group_hint, snippet)
            VALUES ($id, $url, $title, $outlet, $language, $country, $seen, $feed, $hint, $snippet)
            """;
        var parameters = new[] { "$id", "$url", "$title", "$outlet", "$language", "$country", "$seen", "$feed", "$hint", "$snippet" }
            .Select(name => command.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        var added = 0;
        foreach (var a in articles.Where(article => !NewsNoise.IsNoise(article)))
        {
            object?[] values = [a.Id, a.Url, a.Title, a.Outlet, a.Language, a.Country, a.SeenAt.ToUnixTimeSeconds(), a.Feed, a.GroupHint, a.Snippet];
            for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
            var inserted = command.ExecuteNonQuery();
            added += inserted;
            if (inserted > 0)
            {
                hourCountry.Value = a.Country;
                hourStart.Value = a.SeenAt.ToUnixTimeSeconds() / 3600 * 3600;
                hours.ExecuteNonQuery();
                dayOutlet.Value = a.Outlet;
                dayStart.Value = a.SeenAt.ToUnixTimeSeconds() / 86400 * 86400;
                days.ExecuteNonQuery();
                continue;
            }
            existingId.Value = a.Id;
            using var reader = existing.ExecuteReader();
            if (!reader.Read()) continue;
            var first = reader.GetString(0);
            var latest = reader.IsDBNull(1) ? first : reader.GetString(1);
            reader.Close();
            if (!ArticleEdits.IsChanged(latest, a.Title)) continue;
            object[] editValues = [a.Id, latest, a.Title, ArticleEdits.KindOf(a.Title), a.SeenAt.ToUnixTimeSeconds()];
            for (var i = 0; i < editValues.Length; i++) editParameters[i].Value = editValues[i];
            edit.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    // New stories (events) first seen since then, for the crawl dashboard.
    public int CountNewEvents(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events WHERE first_seen >= $since";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // N1: the desk's verification checklist for a story.
    public IReadOnlyList<DeskCheck> DeskChecks(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, item, done, evidence, checked_by, checked_at FROM desk_checks WHERE event_id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var checks = new List<DeskCheck>();
        while (reader.Read())
            checks.Add(new DeskCheck(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
        return checks;
    }

    public void SetDeskCheck(string eventId, string item, bool done, string? evidence, string checkedBy, DateTimeOffset now)
    {
        if (DeskChecklist.Items.All(i => i.Key != item)) throw new ArgumentException("Unknown checklist item.", nameof(item));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO desk_checks (event_id, item, done, evidence, checked_by, checked_at) VALUES ($event, $item, $done, $evidence, $by, $at) "
            + "ON CONFLICT(event_id, item) DO UPDATE SET done = excluded.done, evidence = excluded.evidence, checked_by = excluded.checked_by, checked_at = excluded.checked_at";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$item", item);
        command.Parameters.AddWithValue("$done", done ? 1 : 0);
        command.Parameters.AddWithValue("$evidence", (object?)(evidence?.Trim() is { Length: > 0 } text ? (text.Length > 1000 ? text[..1000] : text) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", checkedBy);
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    // N3: the story budget.
    public IReadOnlyList<BudgetItem> Budget()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, title, owner, status, next_action, updated_at, updated_by FROM budget ORDER BY updated_at DESC";
        using var reader = command.ExecuteReader();
        var items = new List<BudgetItem>();
        while (reader.Read())
            items.Add(new BudgetItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), reader.GetString(6)));
        return items;
    }

    public void SetBudgetItem(BudgetItem item)
    {
        if (!BudgetStatuses.All.Contains(item.Status)) throw new ArgumentException("Unknown status.", nameof(item));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO budget (event_id, title, owner, status, next_action, updated_at, updated_by) VALUES ($event, $title, $owner, $status, $next, $at, $by) "
            + "ON CONFLICT(event_id) DO UPDATE SET title = excluded.title, owner = excluded.owner, status = excluded.status, next_action = excluded.next_action, "
            + "updated_at = excluded.updated_at, updated_by = excluded.updated_by";
        command.Parameters.AddWithValue("$event", item.EventId);
        command.Parameters.AddWithValue("$title", item.Title.Length > 300 ? item.Title[..300] : item.Title);
        command.Parameters.AddWithValue("$owner", item.Owner);
        command.Parameters.AddWithValue("$status", item.Status);
        command.Parameters.AddWithValue("$next", item.NextAction.Length > 500 ? item.NextAction[..500] : item.NextAction);
        command.Parameters.AddWithValue("$at", item.UpdatedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$by", item.UpdatedBy);
        command.ExecuteNonQuery();
    }

    public void RemoveBudgetItem(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM budget WHERE event_id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        command.ExecuteNonQuery();
    }

    // N4: tips. Kept for 90 days unless the desk is following one.
    public NewsTip AddTip(string? name, string? contact, string message, string? url, DateTimeOffset now)
    {
        static string? Cap(string? value, int max) => value?.Trim() is { Length: > 0 } text ? (text.Length > max ? text[..max] : text) : null;
        var tip = new NewsTip(Guid.NewGuid().ToString("N"), Cap(name, TipRules.MaxName), Cap(contact, TipRules.MaxContact), Cap(message, TipRules.MaxMessage)!,
            Cap(url, TipRules.MaxUrl), now, TipStatuses.New, null, null);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tips (id, name, contact, message, url, created_at, status) VALUES ($id, $name, $contact, $message, $url, $at, 'new'); "
            + "DELETE FROM tips WHERE status <> 'following' AND created_at < $old";
        command.Parameters.AddWithValue("$id", tip.Id);
        command.Parameters.AddWithValue("$name", (object?)tip.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$contact", (object?)tip.Contact ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", tip.Message);
        command.Parameters.AddWithValue("$url", (object?)tip.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$old", now.AddDays(-90).ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        return tip;
    }

    public IReadOnlyList<NewsTip> Tips()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, contact, message, url, created_at, status, event_id, handled_by FROM tips ORDER BY created_at DESC LIMIT 200";
        using var reader = command.ExecuteReader();
        var tips = new List<NewsTip>();
        while (reader.Read())
            tips.Add(new NewsTip(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return tips;
    }

    public void SetTipStatus(string id, string status, string? eventId, string handledBy)
    {
        if (status is not (TipStatuses.New or TipStatuses.Following or TipStatuses.Closed)) throw new ArgumentException("Unknown status.", nameof(status));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tips SET status = $status, event_id = COALESCE($event, event_id), handled_by = $by WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$event", (object?)eventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", handledBy);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // The developing story an event belongs to, for "show on Overwatch".
    public bool HasEvent(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM events WHERE id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        return command.ExecuteScalar() is not null;
    }

    public string? SituationOf(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT situation_id FROM events WHERE id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        return command.ExecuteScalar() as string;
    }

    // A3: outlets' current owners from Wikidata, replaced in full by each weekly refresh.
    public IReadOnlyList<(string Outlet, string Owner)> OutletOwners(out DateTimeOffset? fetchedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outlet, owner, fetched_at FROM outlet_owners";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        fetchedAt = null;
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
            fetchedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2));
        }
        return rows;
    }

    public void ReplaceOutletOwners(IEnumerable<(string Outlet, string Owner)> rows, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM outlet_owners");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO outlet_owners (outlet, owner, fetched_at) VALUES ($outlet, $owner, $at)";
        var outlet = command.Parameters.Add(new SqliteParameter { ParameterName = "$outlet" });
        var owner = command.Parameters.Add(new SqliteParameter { ParameterName = "$owner" });
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        foreach (var row in rows)
        {
            outlet.Value = row.Outlet;
            owner.Value = row.Owner;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // A7/A8: labels over time, hash-chained. Only a change of label (level, reason, counts or rules)
    // adds a snapshot. Never pruned: a gap would break the chain.
    public int AppendLabelSnapshots(IEnumerable<(string EventId, string Level, string Reason, int Independent, int Outlets)> labels, string rulesVersion, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string previous;
        using (var last = connection.CreateCommand())
        {
            last.CommandText = "SELECT hash FROM label_snapshots ORDER BY id DESC LIMIT 1";
            previous = last.ExecuteScalar() as string ?? LabelLedger.Genesis;
        }
        using var latest = connection.CreateCommand();
        latest.CommandText = "SELECT level, reason, independent, outlets, rules_version FROM label_snapshots WHERE event_id = $event ORDER BY id DESC LIMIT 1";
        var latestEvent = latest.Parameters.Add(new SqliteParameter { ParameterName = "$event" });
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO label_snapshots (event_id, level, reason, independent, outlets, rules_version, taken_at, previous_hash, hash) "
            + "VALUES ($event, $level, $reason, $independent, $outlets, $rules, $at, $previous, $hash)";
        var names = new[] { "$event", "$level", "$reason", "$independent", "$outlets", "$rules", "$at", "$previous", "$hash" };
        var parameters = names.Select(name => insert.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        var added = 0;
        foreach (var label in labels)
        {
            latestEvent.Value = label.EventId;
            using (var reader = latest.ExecuteReader())
            {
                if (reader.Read() && reader.GetString(0) == label.Level && reader.GetString(1) == label.Reason && reader.GetInt32(2) == label.Independent
                    && reader.GetInt32(3) == label.Outlets && reader.GetString(4) == rulesVersion) continue;
            }
            var hash = LabelLedger.HashOf(previous, label.EventId, label.Level, label.Reason, label.Independent, label.Outlets, rulesVersion, now);
            object[] values = [label.EventId, label.Level, label.Reason, label.Independent, label.Outlets, rulesVersion, now.ToUnixTimeSeconds(), previous, hash];
            for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i];
            insert.ExecuteNonQuery();
            previous = hash;
            added++;
        }
        transaction.Commit();
        return added;
    }

    public IReadOnlyList<LabelSnapshot> LabelSnapshots(string? eventId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, level, reason, independent, outlets, rules_version, taken_at, previous_hash, hash FROM label_snapshots "
            + (eventId is null ? "" : "WHERE event_id = $event ") + "ORDER BY id";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var snapshots = new List<LabelSnapshot>();
        while (reader.Read())
            snapshots.Add(new LabelSnapshot(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetString(6), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)), reader.GetString(8), reader.GetString(9)));
        return snapshots;
    }

    // A7: each story labelled at least minAge ago: its first label, its latest, and whether any of its
    // reports carries a correction.
    public IReadOnlyList<(string First, string Latest, bool Corrected)> CalibrationOutcomes(DateTimeOffset firstLabelledBefore)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.level, l.level, EXISTS (SELECT 1 FROM article_edits ae JOIN articles a ON a.id = ae.article_id WHERE a.event_id = g.event_id AND ae.kind = 'correction')
            FROM (SELECT event_id, MIN(id) AS first_id, MAX(id) AS last_id FROM label_snapshots GROUP BY event_id HAVING MIN(taken_at) <= $before) g
            JOIN label_snapshots f ON f.id = g.first_id JOIN label_snapshots l ON l.id = g.last_id
            """;
        command.Parameters.AddWithValue("$before", firstLabelledBefore.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var outcomes = new List<(string, string, bool)>();
        while (reader.Read()) outcomes.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));
        return outcomes;
    }

    // A9: challenges to a label.
    public LabelChallenge AddChallenge(string eventId, string username, string note, string? evidenceUrl, DateTimeOffset now)
    {
        var challenge = new LabelChallenge(Guid.NewGuid().ToString("N"), eventId, username, note.Trim(), evidenceUrl?.Trim() is { Length: > 0 } url ? url : null,
            now, LabelChallengeStatuses.Open, null, null, null);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO label_challenges (id, event_id, username, note, evidence_url, created_at, status) VALUES ($id, $event, $user, $note, $url, $at, 'open')";
        command.Parameters.AddWithValue("$id", challenge.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$note", challenge.Note);
        command.Parameters.AddWithValue("$url", (object?)challenge.EvidenceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        return challenge;
    }

    public IReadOnlyList<LabelChallenge> Challenges(string? eventId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, username, note, evidence_url, created_at, status, resolved_by, resolution, resolved_at FROM label_challenges "
            + (eventId is null ? "" : "WHERE event_id = $event ") + "ORDER BY created_at DESC";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var challenges = new List<LabelChallenge>();
        while (reader.Read())
            challenges.Add(new LabelChallenge(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9))));
        return challenges;
    }

    public bool ResolveChallenge(string id, bool upheld, string resolvedBy, string resolution, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE label_challenges SET status = $status, resolved_by = $by, resolution = $resolution, resolved_at = $at WHERE id = $id AND status = 'open'";
        command.Parameters.AddWithValue("$status", upheld ? LabelChallengeStatuses.Upheld : LabelChallengeStatuses.Rejected);
        command.Parameters.AddWithValue("$by", resolvedBy);
        command.Parameters.AddWithValue("$resolution", resolution.Trim());
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    // Feeds added on the sources page (1.15) and feeds paused there, built-in or added.
    public IReadOnlyList<NewsOutlet> CustomFeeds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, language, country, url, kind FROM custom_feeds ORDER BY added_at";
        using var reader = command.ExecuteReader();
        var feeds = new List<NewsOutlet>();
        while (reader.Read()) feeds.Add(new NewsOutlet(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return feeds;
    }

    public void AddCustomFeed(NewsOutlet feed, string addedBy, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO custom_feeds (url, name, language, country, kind, added_by, added_at) VALUES ($url, $name, $language, $country, $kind, $by, $at)";
        command.Parameters.AddWithValue("$url", feed.Url);
        command.Parameters.AddWithValue("$name", feed.Name);
        command.Parameters.AddWithValue("$language", feed.Language);
        command.Parameters.AddWithValue("$country", feed.Country);
        command.Parameters.AddWithValue("$kind", feed.Kind);
        command.Parameters.AddWithValue("$by", addedBy);
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void RemoveCustomFeed(string url)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM custom_feeds WHERE url = $url; DELETE FROM paused_feeds WHERE url = $url";
        command.Parameters.AddWithValue("$url", url);
        command.ExecuteNonQuery();
    }

    public IReadOnlySet<string> PausedFeeds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url FROM paused_feeds";
        using var reader = command.ExecuteReader();
        var urls = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) urls.Add(reader.GetString(0));
        return urls;
    }

    public void SetFeedPaused(string url, bool paused)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = paused ? "INSERT OR IGNORE INTO paused_feeds (url) VALUES ($url)" : "DELETE FROM paused_feeds WHERE url = $url";
        command.Parameters.AddWithValue("$url", url);
        command.ExecuteNonQuery();
    }

    // Each outlet's last week (1.18): reports from the daily counts, first reports from events, and
    // headline changes from article_edits. Bounded by the week's events, never a scan of articles.
    public IReadOnlyList<OutletRecord> OutletRecords(DateTimeOffset since)
    {
        using var connection = Open();
        var reports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, (int All, int Corrections)>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT outlet, SUM(reports) FROM outlet_days WHERE day >= $since GROUP BY outlet";
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds() / 86400 * 86400);
            using var reader = command.ExecuteReader();
            while (reader.Read()) reports[reader.GetString(0)] = reader.GetInt32(1);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.outlet, COUNT(DISTINCT e.id) FROM events e JOIN articles a ON a.event_id = e.id AND a.seen_at = e.first_seen "
                + "WHERE e.first_seen >= $since GROUP BY a.outlet";
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            while (reader.Read()) first[reader.GetString(0)] = reader.GetInt32(1);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.outlet, COUNT(*), SUM(CASE WHEN ae.kind = 'correction' THEN 1 ELSE 0 END) FROM article_edits ae "
                + "JOIN articles a ON a.id = ae.article_id WHERE ae.seen_at >= $since GROUP BY a.outlet";
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            while (reader.Read()) changes[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }
        return reports.Keys.Union(first.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(outlet => new OutletRecord(outlet, reports.GetValueOrDefault(outlet), first.GetValueOrDefault(outlet),
                changes.GetValueOrDefault(outlet).All, changes.GetValueOrDefault(outlet).Corrections))
            .OrderByDescending(record => record.Reports)
            .ToList();
    }

    // Reports per country per hour (1.12), kept as reports arrive so silence is found without
    // scanning the articles table.
    public IReadOnlyList<(string Country, DateTimeOffset Hour, int Reports)> CountryHours(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT country, hour, reports FROM country_hours WHERE hour >= $since";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var rows = new List<(string, DateTimeOffset, int)>();
        while (reader.Read()) rows.Add((reader.GetString(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)), reader.GetInt32(2)));
        return rows;
    }

    public IReadOnlyList<CoverageSilence> Silences(DateTimeOffset now) =>
        CoverageSilenceRules.Find(CountryHours(now - CoverageSilenceRules.Window - CoverageSilenceRules.Baseline), now);

    // Watchlists (World News trust plan 1.11, 13-11-26): a person's topics, places, people and
    // companies, each a search (FullTextQuery syntax) run against the last day's reports.
    public const int MaxKeywordWatches = 50;
    public static readonly TimeSpan KeywordWatchWindow = TimeSpan.FromHours(24);

    public IReadOnlyList<KeywordWatch> KeywordWatches(string username)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, query, created_at FROM keyword_watches WHERE username = $user ORDER BY created_at";
        command.Parameters.AddWithValue("$user", username);
        return ReadKeywordWatches(command);
    }

    // The new watch, or null when the query has nothing to search for or the person has the maximum.
    public KeywordWatch? AddKeywordWatch(string username, string query, DateTimeOffset now)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200 || FullTextQuery.From(query) is null) return null;
        var existing = KeywordWatches(username);
        if (existing.FirstOrDefault(w => string.Equals(w.Query, query, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
        if (existing.Count >= MaxKeywordWatches) return null;
        var watch = new KeywordWatch(Guid.NewGuid().ToString("N"), username, query, now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO keyword_watches (id, username, query, created_at) VALUES ($id, $user, $query, $created)";
        command.Parameters.AddWithValue("$id", watch.Id);
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        return watch;
    }

    public void RemoveKeywordWatch(string username, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM keyword_watch_hits WHERE watch_id IN (SELECT id FROM keyword_watches WHERE id = $id AND username = $user); "
            + "DELETE FROM keyword_watches WHERE id = $id AND username = $user";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", username);
        command.ExecuteNonQuery();
    }

    // One alert per watch per story, once the story passes mayAlert (the breaking-news gate). A story
    // held back is looked at again on later reads while its reports are within the last day.
    public IReadOnlyList<WorldNewsAlert> CreateKeywordAlerts(DateTimeOffset now, Func<string, bool>? mayAlert = null)
    {
        using var connection = Open();
        List<KeywordWatch> watches;
        using (var all = connection.CreateCommand())
        {
            all.CommandText = "SELECT id, username, query, created_at FROM keyword_watches";
            watches = ReadKeywordWatches(all);
        }
        if (watches.Count == 0) return [];
        var fullText = IsFullTextBuilt(connection);
        var alerts = new List<WorldNewsAlert>();
        foreach (var watch in watches)
        {
            var since = Math.Max(watch.CreatedAt.ToUnixTimeSeconds(), (now - KeywordWatchWindow).ToUnixTimeSeconds());
            var query = fullText ? FullTextQuery.From(watch.Query) : watch.Query;
            if (query is null) continue;
            var match = fullText
                ? "a.rowid IN (SELECT rowid FROM articles_fts WHERE articles_fts MATCH $q)"
                : "instr(lower(a.title || ' ' || COALESCE(a.snippet, '')), lower($q)) > 0";
            using var find = connection.CreateCommand();
            find.CommandText = "SELECT DISTINCT e.situation_id FROM articles a JOIN events e ON e.id = a.event_id "
                + $"WHERE a.seen_at > $since AND e.situation_id IS NOT NULL AND {match} "
                + "AND NOT EXISTS (SELECT 1 FROM keyword_watch_hits h WHERE h.watch_id = $watch AND h.situation_id = e.situation_id) LIMIT 20";
            find.Parameters.AddWithValue("$q", query);
            find.Parameters.AddWithValue("$since", since);
            find.Parameters.AddWithValue("$watch", watch.Id);
            var situations = new List<string>();
            using (var reader = find.ExecuteReader()) while (reader.Read()) situations.Add(reader.GetString(0));
            foreach (var situationId in situations)
            {
                if (mayAlert is not null && !mayAlert(situationId)) continue;
                var title = Situation(situationId)?.Title ?? "A story";
                var alert = new WorldNewsAlert(Guid.NewGuid(), watch.Username, situationId, title, $"Matches your watchlist: {watch.Query}", now, false);
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO world_news_alerts (id, username, situation_id, title, message, created_at) VALUES ($id, $user, $situation, $title, $message, $created); "
                    + "INSERT OR IGNORE INTO keyword_watch_hits (watch_id, situation_id) VALUES ($watch, $situation)";
                insert.Parameters.AddWithValue("$id", alert.Id.ToString("N"));
                insert.Parameters.AddWithValue("$user", watch.Username);
                insert.Parameters.AddWithValue("$situation", situationId);
                insert.Parameters.AddWithValue("$title", title);
                insert.Parameters.AddWithValue("$message", alert.Message);
                insert.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
                insert.Parameters.AddWithValue("$watch", watch.Id);
                insert.ExecuteNonQuery();
                alerts.Add(alert);
            }
        }
        return alerts;
    }

    private static List<KeywordWatch> ReadKeywordWatches(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var watches = new List<KeywordWatch>();
        while (reader.Read())
            watches.Add(new KeywordWatch(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
        return watches;
    }

    // Every feed's health (FeedSchedule), by URL.
    public IReadOnlyDictionary<string, FeedHealth> FeedHealth()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url, etag, last_modified, last_status, last_attempt, last_success, last_error, failures, empty_reads, added, skip_until FROM feed_health";
        using var reader = command.ExecuteReader();
        var health = new Dictionary<string, FeedHealth>(StringComparer.Ordinal);
        static DateTimeOffset? Time(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(i));
        while (reader.Read())
        {
            health[reader.GetString(0)] = new FeedHealth(reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), Time(reader, 4), Time(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt64(9), Time(reader, 10));
        }
        return health;
    }

    public void SaveFeedHealth(IEnumerable<FeedHealth> feeds)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO feed_health (url, etag, last_modified, last_status, last_attempt, last_success, last_error, failures, empty_reads, added, skip_until)
            VALUES ($url, $etag, $modified, $status, $attempt, $success, $error, $failures, $empty, $added, $skip)
            ON CONFLICT(url) DO UPDATE SET etag = excluded.etag, last_modified = excluded.last_modified, last_status = excluded.last_status,
                last_attempt = excluded.last_attempt, last_success = excluded.last_success, last_error = excluded.last_error,
                failures = excluded.failures, empty_reads = excluded.empty_reads, added = excluded.added, skip_until = excluded.skip_until
            """;
        var names = new[] { "$url", "$etag", "$modified", "$status", "$attempt", "$success", "$error", "$failures", "$empty", "$added", "$skip" };
        var parameters = names.Select(name => command.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        foreach (var feed in feeds)
        {
            object?[] values = [feed.Url, feed.ETag, feed.LastModified, feed.LastStatus, feed.LastAttempt?.ToUnixTimeSeconds(), feed.LastSuccess?.ToUnixTimeSeconds(),
                feed.LastError is { Length: > 300 } error ? error[..300] : feed.LastError, feed.Failures, feed.EmptyReads, feed.Added, feed.SkipUntil?.ToUnixTimeSeconds()];
            for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // Every recorded change to the given reports' headlines, oldest first.
    public IReadOnlyList<ArticleEdit> Edits(IReadOnlyCollection<string> articleIds)
    {
        if (articleIds.Count == 0) return [];
        using var connection = Open();
        using var command = connection.CreateCommand();
        var names = articleIds.Select((id, i) => { command.Parameters.AddWithValue($"$a{i}", id); return $"$a{i}"; });
        command.CommandText = $"SELECT article_id, before, after, kind, seen_at FROM article_edits WHERE article_id IN ({string.Join(',', names)}) ORDER BY seen_at";
        using var reader = command.ExecuteReader();
        var edits = new List<ArticleEdit>();
        while (reader.Read())
            edits.Add(new ArticleEdit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
        return edits;
    }

    public IReadOnlyList<WorldArticle> ArticlesToEmbed(int limit) =>
        QueryArticles("WHERE embedding IS NULL ORDER BY seen_at LIMIT $limit", ("$limit", limit));

    // Newest first, and none older than since: a report too old to join a live event isn't worth
    // the CPU, and on a busy server the oldest-first order spent every read catching up on stale news.
    public IReadOnlyList<WorldArticle> RecentArticlesToEmbed(int limit, DateTimeOffset since) =>
        QueryArticles("WHERE embedding IS NULL AND seen_at >= $since ORDER BY seen_at DESC LIMIT $limit",
            ("$limit", limit), ("$since", since.ToUnixTimeSeconds()));

    public IReadOnlyList<WorldArticle> RecentReports(DateTimeOffset since, int limit = 100, int offset = 0,
        string? text = null, string? country = null, string? language = null, string? outlet = null, string? kind = null)
    {
        var conditions = new List<string> { "seen_at >= $since" };
        var parameters = new List<(string Name, object Value)> { ("$since", since.ToUnixTimeSeconds()) };
        // Full text when the index is built (phrases, AND/OR/NOT, prefix*); before that, or for a
        // query FTS can't read, the substring match.
        if (!string.IsNullOrWhiteSpace(text) && FullTextReady())
        {
            if (FullTextQuery.From(text) is { } fullText)
            {
                conditions.Add("rowid IN (SELECT rowid FROM articles_fts WHERE articles_fts MATCH $fts)");
                parameters.Add(("$fts", fullText));
            }
        }
        else AddTextFilter("title || ' ' || COALESCE(snippet, '')", text, "$text");
        AddTextFilter("country", country, "$country");
        AddTextFilter("language", language, "$language");
        AddTextFilter("outlet", outlet, "$outlet");

        const string classified = "SELECT 1 FROM events e JOIN situations s ON s.id = e.situation_id "
            + "WHERE e.id = articles.event_id AND s.kind IS NOT NULL";
        if (string.Equals(kind, "unclassified", StringComparison.OrdinalIgnoreCase))
            conditions.Add($"NOT EXISTS ({classified})");
        else if (!string.IsNullOrWhiteSpace(kind))
        {
            conditions.Add("EXISTS (SELECT 1 FROM events e JOIN situations s ON s.id = e.situation_id "
                + "WHERE e.id = articles.event_id AND s.kind = $kind)");
            parameters.Add(("$kind", kind));
        }

        parameters.Add(("$limit", Math.Clamp(limit, 0, 500)));
        parameters.Add(("$offset", Math.Max(0, offset)));
        return QueryArticles($"WHERE {string.Join(" AND ", conditions)} ORDER BY seen_at DESC, id LIMIT $limit OFFSET $offset",
            parameters.ToArray());

        void AddTextFilter(string column, string? value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            conditions.Add($"instr(lower({column}), lower({parameter})) > 0");
            parameters.Add((parameter, value.Trim()));
        }
    }

    public int RecentEmbeddingBacklog(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM articles WHERE embedding IS NULL AND seen_at >= $since";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public DateTimeOffset? LatestReportAt(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(seen_at) FROM articles WHERE seen_at >= $since";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value));
    }

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

    // Embeddings from the Mac app (WorldNewsEmbedRelay): only for reports still waiting for one,
    // so a report the server embedded or grouped meanwhile keeps what it has. Returns how many.
    public int FillEmbeddings(IReadOnlyList<(string Id, float[] Vector)> embeddings)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET embedding = $vector WHERE id = $id AND embedding IS NULL";
        var id = command.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
        var vector = command.Parameters.Add(new SqliteParameter { ParameterName = "$vector" });
        var filled = 0;
        foreach (var (articleId, values) in embeddings)
        {
            id.Value = articleId;
            vector.Value = ToBytes(values);
            filled += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return filled;
    }

    // How many numbers the stored vectors have, from an event's (null before the first event), so
    // vectors from a different model can be turned away.
    public int? EmbeddingDimensions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Skips events whose vector was released (ReleaseStaleVectors).
        command.CommandText = "SELECT length(vector_sum) / 4 FROM events WHERE length(vector_sum) > 0 LIMIT 1";
        return command.ExecuteScalar() is long dimensions ? (int)dimensions : null;
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
            // A report's embedding only serves to group it; once it's in an event (whose own vector
            // carries the meaning) it's dropped. Embeddings were ~1.3 GB of the droplet's 2 GB of
            // reports (30-10-26). Empty, not NULL: NULL means "still to embed".
            assign.CommandText = "UPDATE articles SET event_id = $event, embedding = X'' WHERE id = $id";
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
                DELETE FROM analyses WHERE event_id = $drop;
                DELETE FROM analysis_history WHERE event_id = $drop;
                DELETE FROM story_reads WHERE event_id = $drop;
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

    // The same situation list as it was known at a point in time. Replay must aggregate articles
    // by their seen time: filtering today's already-aggregated situations would leak later reports.
    public IReadOnlyList<SituationOverview> TopSituationsAt(DateTimeOffset since, DateTimeOffset at, int limit) =>
        Situations(since, limit, onlyId: null, at);

    // One situation straight from its id (a link, a panel), rather than ranking every one.
    public SituationOverview? Situation(string situationId) =>
        Situations(DateTimeOffset.UnixEpoch, 1, onlyId: situationId).FirstOrDefault();

    private IReadOnlyList<SituationOverview> Situations(DateTimeOffset since, int limit, string? onlyId, DateTimeOffset? at = null)
    {
        using var connection = Open();
        var rows = new List<(string Id, int Events, int Articles, int Outlets, string? Countries, string? Languages, long First, long Last, string? Kind)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = at is null ? """
                SELECT e.situation_id, COUNT(DISTINCT e.id), COUNT(*), COUNT(DISTINCT a.outlet),
                       GROUP_CONCAT(DISTINCT a.country), GROUP_CONCAT(DISTINCT a.language), MIN(e.first_seen), MAX(e.last_seen),
                       (SELECT kind FROM situations s WHERE s.id = e.situation_id)
                FROM events e JOIN articles a ON a.event_id = e.id
                WHERE e.situation_id IN (SELECT situation_id FROM events WHERE situation_id IS NOT NULL
                                         AND ($only IS NULL OR situation_id = $only)
                                         GROUP BY situation_id HAVING COUNT(*) >= 2 AND MAX(last_seen) >= $since)
                GROUP BY e.situation_id
                ORDER BY COUNT(DISTINCT a.outlet) DESC, MAX(e.last_seen) DESC LIMIT $limit
                """ : """
                SELECT e.situation_id, COUNT(DISTINCT e.id), COUNT(*), COUNT(DISTINCT a.outlet),
                       GROUP_CONCAT(DISTINCT a.country), GROUP_CONCAT(DISTINCT a.language), MIN(e.first_seen), MAX(a.seen_at),
                       (SELECT kind FROM situations s WHERE s.id = e.situation_id)
                FROM events e JOIN articles a ON a.event_id = e.id
                WHERE e.situation_id IN (
                    SELECT e2.situation_id FROM events e2 JOIN articles a2 ON a2.event_id = e2.id
                    WHERE e2.situation_id IS NOT NULL AND e2.first_seen <= $at AND a2.seen_at <= $at
                      AND ($only IS NULL OR e2.situation_id = $only)
                    GROUP BY e2.situation_id HAVING COUNT(DISTINCT e2.id) >= 2 AND MAX(a2.seen_at) >= $since)
                  AND e.first_seen <= $at AND a.seen_at <= $at
                GROUP BY e.situation_id
                ORDER BY COUNT(DISTINCT a.outlet) DESC, MAX(a.seen_at) DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$at", at is { } replayAt ? replayAt.ToUnixTimeSeconds() : DBNull.Value);
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
                FROM events e WHERE situation_id IN ({string.Join(", ", names)}){(at is null ? "" : " AND first_seen <= $at")}
                """;
            for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue(names[i], rows[i].Id);
            if (at is not null) command.Parameters.AddWithValue("$at", at.Value.ToUnixTimeSeconds());
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
    // mayAlert (the breaking-news gate, 13-11-26) can hold a situation back: its count isn't
    // updated, so it alerts on a later read once the gate lets it through.
    public IReadOnlyList<WorldNewsAlert> CreateWatchAlerts(DateTimeOffset now, Func<string, bool>? mayAlert = null)
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
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in pending)
        {
            if (mayAlert is not null && !(allowed.TryGetValue(item.SituationId, out var ok) ? ok : allowed[item.SituationId] = mayAlert(item.SituationId))) continue;
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
    // yet, have grown by a quarter (and at least 3 reports) since their latest one, or whose latest
    // one is from before comparisons (asked once more, so current events get one).
    public IReadOnlyList<EventOverview> EventsDueSummary(DateTimeOffset since, int minOutlets, int limit) =>
        Overviews("WHERE e.last_seen >= $since", """
            HAVING COUNT(DISTINCT a.outlet) >= $min AND (COALESCE(e.article_count >= (
                SELECT article_count + MAX(3, article_count / 4) FROM summaries
                WHERE event_id = e.id ORDER BY version DESC LIMIT 1), 1)
                OR (SELECT comparison IS NULL FROM summaries WHERE event_id = e.id ORDER BY version DESC LIMIT 1) = 1)
            """, ("$since", since.ToUnixTimeSeconds()), ("$min", minOutlets), ("$limit", limit));

    public EventSummary? LatestSummary(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, version, headline, summary, model, article_count, created_at, comparison FROM summaries
            WHERE event_id = $event ORDER BY version DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EventSummary(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            : null;
    }

    // Stores the next version of an event's summary (earlier versions are kept) and returns it.
    public EventSummary SaveSummary(string eventId, string? headline, string text, string model, int articleCount, DateTimeOffset createdAt,
        IReadOnlyList<string>? comparison = null)
    {
        IReadOnlyList<string> points = (comparison ?? []).Select(point => point.Replace('\n', ' ').Trim()).Where(point => point.Length > 0).ToList();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO summaries (event_id, version, headline, summary, model, article_count, created_at, comparison)
            VALUES ($event, (SELECT COALESCE(MAX(version), 0) + 1 FROM summaries WHERE event_id = $event),
                    $headline, $summary, $model, $count, $created, $comparison)
            RETURNING version
            """;
        command.Parameters.AddWithValue("$comparison", string.Join('\n', points));
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$headline", (object?)headline ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", text);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$count", articleCount);
        command.Parameters.AddWithValue("$created", createdAt.ToUnixTimeSeconds());
        var version = Convert.ToInt32(command.ExecuteScalar());
        return new EventSummary(eventId, version, headline, text, model, articleCount, DateTimeOffset.FromUnixTimeSeconds(createdAt.ToUnixTimeSeconds()), points);
    }

    // SentinelGPT's analysis of an event (EventAnalyzer): only the latest is kept.
    public EventAnalysis? LatestAnalysis(string eventId) =>
        ReadAnalysis(eventId, "SELECT claim, summary, agreed, differences, model, article_count, articles_read, created_at FROM analyses WHERE event_id = $event");

    // The analysis a reader saw at analysisAt: the latest if that's it, else the kept version.
    public EventAnalysis? AnalysisAt(string eventId, DateTimeOffset analysisAt) =>
        LatestAnalysis(eventId) is { } latest && latest.CreatedAt.ToUnixTimeSeconds() == analysisAt.ToUnixTimeSeconds()
            ? latest
            : ReadAnalysis(eventId, """
                SELECT claim, summary, agreed, differences, model, article_count, articles_read, created_at FROM analysis_history
                WHERE event_id = $event AND created_at <= $at ORDER BY created_at DESC LIMIT 1
                """, ("$at", analysisAt.ToUnixTimeSeconds()));

    // Versions kept for "what changed since you last read".
    public const int KeptAnalysisVersions = 10;

    private EventAnalysis? ReadAnalysis(string eventId, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$event", eventId);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var differences = reader.GetString(3).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Select(parts => parts.Length == 2 ? new OutletDifference(parts[0], parts[1]) : new OutletDifference(string.Empty, parts[0]))
            .ToList();
        var agreed = reader.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Select(parts => new AgreedFact(parts[0], parts.Length == 2 ? parts[1].Split('|', StringSplitOptions.RemoveEmptyEntries) : []))
            .ToList();
        return new EventAnalysis(eventId, reader.GetString(0), reader.GetString(1), agreed,
            differences, reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)));
    }

    public EventAnalysis SaveAnalysis(EventAnalysis analysis)
    {
        static string Line(string text) => text.Replace('\n', ' ').Replace('\t', ' ').Trim();
        using var connection = Open();
        using var command = connection.CreateCommand();
        // The one it replaces is kept, for "what changed since you last read".
        command.CommandText = $"""
            INSERT OR IGNORE INTO analysis_history SELECT event_id, claim, summary, agreed, differences, model, article_count, articles_read, created_at
                FROM analyses WHERE event_id = $event;
            DELETE FROM analysis_history WHERE event_id = $event AND created_at NOT IN
                (SELECT created_at FROM analysis_history WHERE event_id = $event ORDER BY created_at DESC LIMIT {KeptAnalysisVersions});
            INSERT OR REPLACE INTO analyses (event_id, claim, summary, agreed, differences, model, article_count, articles_read, created_at)
            VALUES ($event, $claim, $summary, $agreed, $differences, $model, $count, $read, $created)
            """;
        command.Parameters.AddWithValue("$event", analysis.EventId);
        command.Parameters.AddWithValue("$claim", Line(analysis.Claim));
        command.Parameters.AddWithValue("$summary", Line(analysis.Summary));
        command.Parameters.AddWithValue("$agreed", string.Join('\n', analysis.Agreed.Select(fact =>
            $"{Line(fact.Text)}\t{string.Join('|', fact.Outlets.Select(outlet => Line(outlet).Replace('|', '/')))}")));
        command.Parameters.AddWithValue("$differences", string.Join('\n', analysis.Differences.Select(d => $"{Line(d.Outlet)}\t{Line(d.Text)}")));
        command.Parameters.AddWithValue("$model", analysis.Model);
        command.Parameters.AddWithValue("$count", analysis.ArticleCount);
        command.Parameters.AddWithValue("$read", analysis.ArticlesRead);
        command.Parameters.AddWithValue("$created", analysis.CreatedAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        return LatestAnalysis(analysis.EventId)!;
    }

    // What was read from articles' own pages: the excerpt, or null with the reason it couldn't be.
    public IReadOnlyDictionary<string, (string? Excerpt, DateTimeOffset FetchedAt)> ArticleTexts(IEnumerable<string> articleIds)
    {
        var ids = articleIds.Distinct(StringComparer.Ordinal).ToList();
        var found = new Dictionary<string, (string?, DateTimeOffset)>(StringComparer.Ordinal);
        if (ids.Count == 0) return found;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT article_id, excerpt, fetched_at FROM article_texts WHERE article_id IN ({string.Join(',', ids.Select((_, i) => $"$id{i}"))})";
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue($"$id{i}", ids[i]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            found[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
        return found;
    }

    // When a reader last read a story, and which analysis they saw then.
    public (DateTimeOffset AnalysisAt, DateTimeOffset ReadAt)? StoryRead(string username, string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT analysis_at, read_at FROM story_reads WHERE username = $user AND event_id = $event";
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))) : null;
    }

    public void MarkStoryRead(string username, string eventId, DateTimeOffset analysisAt, DateTimeOffset readAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO story_reads (username, event_id, analysis_at, read_at) VALUES ($user, $event, $analysis, $read)";
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$analysis", analysisAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$read", readAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    // English headlines for reports in other languages, by article id.
    public IReadOnlyDictionary<string, string> Translations(IEnumerable<string> articleIds)
    {
        var ids = articleIds.Distinct(StringComparer.Ordinal).ToList();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(500))
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT article_id, english FROM article_translations WHERE article_id IN ({string.Join(',', batch.Select((_, i) => $"$id{i}"))})";
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$id{i}", batch[i]);
            using var reader = command.ExecuteReader();
            while (reader.Read()) found[reader.GetString(0)] = reader.GetString(1);
        }
        return found;
    }

    // An event's English headlines, by article id.
    public IReadOnlyDictionary<string, string> TranslationsForEvent(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.article_id, t.english FROM article_translations t JOIN articles a ON a.id = t.article_id WHERE a.event_id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) found[reader.GetString(0)] = reader.GetString(1);
        return found;
    }

    // An event's articles that were read in full (they have an excerpt).
    public IReadOnlySet<string> ReadInFull(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.article_id FROM article_texts t JOIN articles a ON a.id = t.article_id WHERE a.event_id = $event AND t.excerpt IS NOT NULL";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) found.Add(reader.GetString(0));
        return found;
    }

    public void SaveTranslations(IReadOnlyDictionary<string, string> english, DateTimeOffset at)
    {
        if (english.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (id, text) in english)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO article_translations (article_id, english, created_at) VALUES ($id, $text, $at)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private readonly object _articleTextWrites = new();

    // B4 (13-11-26): an article whose body changed after World News first read it. The first text
    // is kept; later changes update the latest text and time.
    public void SaveBodyEdit(string articleId, string firstText, string latestText, DateTimeOffset detectedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO article_body_edits (article_id, first_text, latest_text, detected_at) VALUES ($id, $first, $latest, $at)
            ON CONFLICT(article_id) DO UPDATE SET latest_text = excluded.latest_text, detected_at = excluded.detected_at
            """;
        command.Parameters.AddWithValue("$id", articleId);
        command.Parameters.AddWithValue("$first", firstText);
        command.Parameters.AddWithValue("$latest", latestText);
        command.Parameters.AddWithValue("$at", detectedAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, (string FirstText, string LatestText, DateTimeOffset DetectedAt)> BodyEdits(IEnumerable<string> articleIds)
    {
        var ids = articleIds.Distinct(StringComparer.Ordinal).Take(500).ToList();
        var edits = new Dictionary<string, (string, string, DateTimeOffset)>(StringComparer.Ordinal);
        if (ids.Count == 0) return edits;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT article_id, first_text, latest_text, detected_at FROM article_body_edits WHERE article_id IN ({string.Join(",", ids.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue("$p" + i, ids[i]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            edits[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)));
        return edits;
    }

    public void SaveArticleText(string articleId, string? excerpt, string? reason, DateTimeOffset fetchedAt)
    {
        lock (_articleTextWrites)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO article_texts (article_id, excerpt, reason, fetched_at) VALUES ($id, $excerpt, $reason, $at)";
            command.Parameters.AddWithValue("$id", articleId);
            command.Parameters.AddWithValue("$excerpt", (object?)excerpt ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", fetchedAt.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<WorldArticle> ArticlesForEvent(string eventId) =>
        QueryArticles("WHERE event_id = $event ORDER BY seen_at", ("$event", eventId));

    public long IngestionCursor(string source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_id FROM ingestion_cursors WHERE source = $source";
        command.Parameters.AddWithValue("$source", source);
        return command.ExecuteScalar() is long lastId ? lastId : 0;
    }

    public void SetIngestionCursor(string source, long lastId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ingestion_cursors (source, last_id) VALUES ($source, $lastId)
            ON CONFLICT(source) DO UPDATE SET last_id = excluded.last_id
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$lastId", lastId);
        command.ExecuteNonQuery();
    }

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
    // The one-time catch-up for reports grouped before embeddings were dropped on grouping: a
    // batch of rowids at a time, so the reader's own writes only ever wait a moment.
    public int ReleaseGroupedEmbeddings(int batchSize = 2_000, Action? betweenBatches = null)
    {
        using var connection = Open();
        long last;
        using (var max = connection.CreateCommand())
        {
            max.CommandText = "SELECT COALESCE(MAX(rowid), 0) FROM articles";
            last = (long)max.ExecuteScalar()!;
        }

        var released = 0;
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET embedding = X'' WHERE rowid > $from AND rowid <= $to AND event_id IS NOT NULL AND length(embedding) > 0";
        var from = command.Parameters.Add(new SqliteParameter { ParameterName = "$from" });
        var to = command.Parameters.Add(new SqliteParameter { ParameterName = "$to" });
        for (long start = 0; start < last; start += batchSize)
        {
            from.Value = start;
            to.Value = start + batchSize;
            released += command.ExecuteNonQuery();
            betweenBatches?.Invoke();
        }
        return released;
    }

    // Size (13-11-26): an event's meaning vector (3 KB) is only read while it can still gain reports
    // (EventWindow, 48 h), by SentinelGPT's search (7 days) and, until it's placed in a situation, by the
    // linker (14 days). So a placed event's vector goes after VectorsFor, an unplaced one's after
    // UnplacedVectorsFor; an article's group hint (read within 48 h) after HintsFor. A batch at a time,
    // so writers only wait a moment.
    public static readonly TimeSpan VectorsFor = TimeSpan.FromDays(8);
    public static readonly TimeSpan UnplacedVectorsFor = TimeSpan.FromDays(15);
    public static readonly TimeSpan HintsFor = TimeSpan.FromDays(3);

    public int ReleaseStaleVectors(DateTimeOffset now, int batchSize = 2_000, Action? betweenBatches = null)
    {
        using var connection = Open();
        var released = 0;
        foreach (var (sql, cutoff) in new[]
        {
            ("UPDATE events SET vector_sum = X'' WHERE rowid IN (SELECT rowid FROM events WHERE last_seen < $cutoff AND situation_id IS NOT NULL AND length(vector_sum) > 0 LIMIT $batch)", now - VectorsFor),
            ("UPDATE events SET vector_sum = X'' WHERE rowid IN (SELECT rowid FROM events WHERE last_seen < $cutoff AND length(vector_sum) > 0 LIMIT $batch)", now - UnplacedVectorsFor),
            ("UPDATE articles SET group_hint = NULL WHERE rowid IN (SELECT rowid FROM articles WHERE seen_at < $cutoff AND group_hint IS NOT NULL LIMIT $batch)", now - HintsFor),
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$batch", batchSize);
            int changed;
            do
            {
                changed = command.ExecuteNonQuery();
                released += changed;
                if (changed > 0) betweenBatches?.Invoke();
            }
            while (changed == batchSize);
        }
        return released;
    }

    // Gives the space back after ReleaseGroupedEmbeddings: a row that shrank leaves a half-empty
    // page rather than a free one, so only rewriting the file packs them again. Minutes on the
    // droplet, so it's for a quiet background moment.
    public void Compact()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    public void Prune(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM articles WHERE seen_at < $before;
            DELETE FROM country_hours WHERE hour < $before;
            DELETE FROM outlet_days WHERE day < $before;
            DELETE FROM events WHERE last_seen < $before;
            DELETE FROM summaries WHERE event_id NOT IN (SELECT id FROM events);
            DELETE FROM analyses WHERE event_id NOT IN (SELECT id FROM events);
            DELETE FROM analysis_history WHERE event_id NOT IN (SELECT id FROM events);
            DELETE FROM story_reads WHERE event_id NOT IN (SELECT id FROM events);
            DELETE FROM article_texts WHERE article_id NOT IN (SELECT id FROM articles);
            DELETE FROM article_translations WHERE article_id NOT IN (SELECT id FROM articles);
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
