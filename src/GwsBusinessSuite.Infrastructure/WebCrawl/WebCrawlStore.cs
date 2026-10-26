using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GwsBusinessSuite.Infrastructure.WebCrawl;

public sealed record FrontierEntry(string Url, string Host, bool Hub, int Depth);

public sealed record StoredHit(long Id, string Url, string Host, string Title, long? PublishedUnix, long FetchedUnix, string Snippet);

public sealed record StoredSite(string Host, string Home, bool ByHand, int Pages, long? LastFetchUnix, string? LastError);

// The crawler's own SQLite file (WebCrawl:DatabasePath), apart from the app's database like World
// News's: the sites it knows, the frontier of addresses to fetch, and the pages it kept, with a
// full-text index (FTS5) over their titles and text. No migrations - the schema creates itself.
public sealed class WebCrawlStore
{
    // Addresses waiting per site, so one huge site can't fill the frontier.
    public const int MaxQueuedPerSite = 3000;
    private readonly string _connectionString;
    // One writer at a time; SQLite would otherwise make parallel fetch tasks wait on its lock.
    private readonly SemaphoreSlim _write = new(1, 1);

    public WebCrawlStore(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 30 }.ToString();
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sites (
                host TEXT PRIMARY KEY, home TEXT NOT NULL, by_hand INTEGER NOT NULL DEFAULT 0, added INTEGER NOT NULL,
                robots TEXT, robots_at INTEGER, delay_ms INTEGER NOT NULL, next_at INTEGER NOT NULL DEFAULT 0,
                pages INTEGER NOT NULL DEFAULT 0, last_fetch INTEGER, last_error TEXT, removed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS frontier (
                url TEXT PRIMARY KEY, host TEXT NOT NULL, hub INTEGER NOT NULL, every_s INTEGER NOT NULL DEFAULT 0,
                depth INTEGER NOT NULL, priority INTEGER NOT NULL, due INTEGER NOT NULL, fetched INTEGER, status INTEGER);
            CREATE INDEX IF NOT EXISTS ix_frontier_due ON frontier (due);
            CREATE INDEX IF NOT EXISTS ix_frontier_host ON frontier (host);
            CREATE TABLE IF NOT EXISTS pages (
                id INTEGER PRIMARY KEY, url TEXT NOT NULL UNIQUE, host TEXT NOT NULL, title TEXT NOT NULL,
                published INTEGER, fetched INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_pages_fetched ON pages (fetched);
            CREATE INDEX IF NOT EXISTS ix_pages_host ON pages (host);
            CREATE VIRTUAL TABLE IF NOT EXISTS pages_fts USING fts5(title, body, tokenize = 'porter unicode61 remove_diacritics 2');
            CREATE TABLE IF NOT EXISTS counters (day INTEGER NOT NULL, name TEXT NOT NULL, value INTEGER NOT NULL, PRIMARY KEY (day, name));
            CREATE TABLE IF NOT EXISTS marks (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            """);
    }

    // A site is created the first time one of its addresses is queued (or it's added by hand).
    public async Task EnsureSiteAsync(string host, string home, long nowMs, int delayMs, bool byHand = false)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = byHand
                ? """
                  INSERT INTO sites (host, home, by_hand, added, delay_ms) VALUES ($host, $home, 1, $now, $delay)
                  ON CONFLICT (host) DO UPDATE SET by_hand = 1, removed = 0, home = excluded.home
                  """
                : "INSERT OR IGNORE INTO sites (host, home, added, delay_ms) VALUES ($host, $home, $now, $delay)";
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$home", home);
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$delay", delayMs);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<bool> SiteKnownAsync(string host)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sites WHERE host = $host";
        command.Parameters.AddWithValue("$host", host);
        return await command.ExecuteScalarAsync() is not null;
    }

    // Queues addresses not seen before (a hub - feed, sitemap, home page - is fetched again every
    // everySeconds). Returns how many were new.
    public async Task<int> EnqueueAsync(IEnumerable<(string Url, string Host, bool Hub, int EverySeconds, int Depth, int Priority)> entries, long dueMs)
    {
        var list = entries.ToList();
        if (list.Count == 0) return 0;
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var queued = new Dictionary<string, long>(StringComparer.Ordinal);
            var added = 0;
            foreach (var entry in list)
            {
                if (!queued.TryGetValue(entry.Host, out var count))
                {
                    using var countCommand = connection.CreateCommand();
                    countCommand.Transaction = transaction;
                    countCommand.CommandText = "SELECT COUNT(*) FROM frontier WHERE host = $host AND fetched IS NULL";
                    countCommand.Parameters.AddWithValue("$host", entry.Host);
                    count = (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
                }
                if (count >= MaxQueuedPerSite && !entry.Hub)
                {
                    queued[entry.Host] = count;
                    continue;
                }
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO frontier (url, host, hub, every_s, depth, priority, due)
                    VALUES ($url, $host, $hub, $every, $depth, $priority, $due)
                    """;
                command.Parameters.AddWithValue("$url", entry.Url);
                command.Parameters.AddWithValue("$host", entry.Host);
                command.Parameters.AddWithValue("$hub", entry.Hub ? 1 : 0);
                command.Parameters.AddWithValue("$every", entry.EverySeconds);
                command.Parameters.AddWithValue("$depth", entry.Depth);
                command.Parameters.AddWithValue("$priority", entry.Priority);
                command.Parameters.AddWithValue("$due", dueMs);
                var inserted = await command.ExecuteNonQueryAsync();
                added += inserted;
                queued[entry.Host] = count + inserted;
            }
            transaction.Commit();
            return added;
        }
        finally
        {
            _write.Release();
        }
    }

    // The next address for up to maxSites sites whose politeness wait is over: hubs that are due
    // again, then unfetched pages, most important first. Each chosen site is held until its
    // fetch is recorded (MarkFetchedAsync sets its next turn).
    public async Task<IReadOnlyList<FrontierEntry>> TakeNextAsync(long nowMs, int maxSites)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.url, f.host, f.hub, f.depth FROM frontier f JOIN sites s ON s.host = f.host
                WHERE s.removed = 0 AND s.next_at <= $now AND f.due <= $now AND (f.fetched IS NULL OR f.hub = 1)
                ORDER BY f.priority DESC, f.due ASC LIMIT 400
                """;
            command.Parameters.AddWithValue("$now", nowMs);
            var chosen = new List<FrontierEntry>();
            using (var reader = await command.ExecuteReaderAsync())
            {
                var hosts = new HashSet<string>(StringComparer.Ordinal);
                while (await reader.ReadAsync() && chosen.Count < maxSites)
                {
                    var host = reader.GetString(1);
                    if (hosts.Add(host)) chosen.Add(new FrontierEntry(reader.GetString(0), host, reader.GetInt64(2) == 1, reader.GetInt32(3)));
                }
            }
            foreach (var entry in chosen)
            {
                using var hold = connection.CreateCommand();
                hold.CommandText = "UPDATE sites SET next_at = $later WHERE host = $host";
                hold.Parameters.AddWithValue("$later", nowMs + 10 * 60_000);
                hold.Parameters.AddWithValue("$host", entry.Host);
                await hold.ExecuteNonQueryAsync();
            }
            return chosen;
        }
        finally
        {
            _write.Release();
        }
    }

    // Puts a site off (its robots.txt couldn't be read) without touching its addresses.
    public async Task DeferSiteAsync(string host, long untilMs, string error)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sites SET next_at = $until, last_error = $error WHERE host = $host";
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$until", untilMs);
            command.Parameters.AddWithValue("$error", error);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _write.Release();
        }
    }

    // Records a fetch: a page is done, a hub comes due again after its interval; the site's next
    // turn is nextSiteMs. status: HTTP status, or 0 when nothing came back.
    public async Task MarkFetchedAsync(string url, string host, int status, long nowMs, long nextSiteMs, string? error)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE frontier SET fetched = $now, status = $status,
                    due = CASE WHEN hub = 1 THEN $now + every_s * 1000 ELSE due END
                WHERE url = $url;
                UPDATE sites SET next_at = $next, last_fetch = $now, last_error = $error WHERE host = $host;
                """;
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$next", nextSiteMs);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<(string? Text, long? FetchedMs, int DelayMs)> GetRobotsAsync(string host)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT robots, robots_at, delay_ms FROM sites WHERE host = $host";
        command.Parameters.AddWithValue("$host", host);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null, 0);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt32(2));
    }

    public async Task SaveRobotsAsync(string host, string text, long nowMs, int delayMs)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sites SET robots = $text, robots_at = $now, delay_ms = $delay WHERE host = $host";
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$text", text.Length > 200_000 ? text[..200_000] : text);
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$delay", delayMs);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _write.Release();
        }
    }

    // Keeps a page and indexes it; a page already kept (same address) is left as it was.
    public async Task<bool> AddPageAsync(string url, string host, string title, DateTimeOffset? published, long fetchedUnix, string body)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO pages (url, host, title, published, fetched) VALUES ($url, $host, $title, $published, $fetched);
                SELECT CASE WHEN changes() = 1 THEN last_insert_rowid() ELSE 0 END;
                """;
            insert.Parameters.AddWithValue("$url", url);
            insert.Parameters.AddWithValue("$host", host);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$published", (object?)published?.ToUnixTimeSeconds() ?? DBNull.Value);
            insert.Parameters.AddWithValue("$fetched", fetchedUnix);
            var id = (long)(await insert.ExecuteScalarAsync() ?? 0L);
            if (id == 0) return false;
            using var index = connection.CreateCommand();
            index.Transaction = transaction;
            index.CommandText = """
                INSERT INTO pages_fts (rowid, title, body) VALUES ($id, $title, $body);
                UPDATE sites SET pages = pages + 1 WHERE host = $host;
                """;
            index.Parameters.AddWithValue("$id", id);
            index.Parameters.AddWithValue("$title", title);
            index.Parameters.AddWithValue("$body", body);
            index.Parameters.AddWithValue("$host", host);
            await index.ExecuteNonQueryAsync();
            transaction.Commit();
            return true;
        }
        finally
        {
            _write.Release();
        }
    }

    // Best matches first (title weighs more than text), or oldestFirst - how topic matching works
    // through pages kept after afterId (and since fetchedSinceUnix) and picks up where it left off.
    public async Task<IReadOnlyList<StoredHit>> SearchAsync(string ftsQuery, int limit, long afterId = 0, long fetchedSinceUnix = 0, bool oldestFirst = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id, p.url, p.host, p.title, p.published, p.fetched, snippet(pages_fts, 1, '', '', ' … ', 28)
            FROM pages_fts JOIN pages p ON p.id = pages_fts.rowid
            WHERE pages_fts MATCH $query AND p.id > $after AND p.fetched >= $since
            ORDER BY {(oldestFirst ? "p.id" : "bm25(pages_fts, 5.0, 1.0)")}
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$since", fetchedSinceUnix);
        command.Parameters.AddWithValue("$limit", limit);
        var hits = new List<StoredHit>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hits.Add(new StoredHit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }
        return hits;
    }

    public async Task<long> MaxPageIdAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM pages";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<long?> GetMarkAsync(string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM marks WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is long value ? value : null;
    }

    public async Task SetMarkAsync(string name, long value)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO marks (name, value) VALUES ($name, $value) ON CONFLICT (name) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<long> AddToCounterAsync(DateOnly day, string name, long by)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO counters (day, name, value) VALUES ($day, $name, $by)
                ON CONFLICT (day, name) DO UPDATE SET value = value + $by;
                SELECT value FROM counters WHERE day = $day AND name = $name;
                """;
            command.Parameters.AddWithValue("$day", DayNumber(day));
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$by", by);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<long> GetCounterAsync(DateOnly day, string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM counters WHERE day = $day AND name = $name";
        command.Parameters.AddWithValue("$day", DayNumber(day));
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is long value ? value : 0;
    }

    public async Task<(long Pages, int Sites, int Queued, long? LastFetchMs)> GetStatsAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM pages),
                   (SELECT COUNT(*) FROM sites WHERE removed = 0),
                   (SELECT COUNT(*) FROM frontier WHERE fetched IS NULL),
                   (SELECT MAX(last_fetch) FROM sites)
            """;
        using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    public async Task<IReadOnlyList<StoredSite>> ListSitesAsync(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT host, home, by_hand, pages, last_fetch, last_error FROM sites WHERE removed = 0
            ORDER BY by_hand DESC, pages DESC, host LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var sites = new List<StoredSite>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sites.Add(new StoredSite(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1, reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return sites;
    }

    // Stops crawling a site and forgets its pages; it stays known (removed) so links to it don't
    // bring it back.
    public async Task RemoveSiteAsync(string host)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sites SET removed = 1, by_hand = 0, pages = 0 WHERE host = $host;
                DELETE FROM frontier WHERE host = $host;
                DELETE FROM pages_fts WHERE rowid IN (SELECT id FROM pages WHERE host = $host);
                DELETE FROM pages WHERE host = $host;
                """;
            command.Parameters.AddWithValue("$host", host);
            await command.ExecuteNonQueryAsync();
            transaction.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    // Pages kept before pagesBeforeUnix go (with their index rows); fetched page addresses are
    // forgotten after forgetBeforeMs, so a page can only come back once it's aged out.
    public async Task<int> PruneAsync(long pagesBeforeUnix, long forgetBeforeMs)
    {
        await _write.WaitAsync();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM pages_fts WHERE rowid IN (SELECT id FROM pages WHERE fetched < $pages);
                UPDATE sites SET pages = (SELECT COUNT(*) FROM pages p WHERE p.host = sites.host AND p.fetched >= $pages);
                DELETE FROM pages WHERE fetched < $pages;
                SELECT changes();
                """;
            command.Parameters.AddWithValue("$pages", pagesBeforeUnix);
            var removed = Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0, CultureInfo.InvariantCulture);
            using var forget = connection.CreateCommand();
            forget.Transaction = transaction;
            forget.CommandText = "DELETE FROM frontier WHERE hub = 0 AND fetched IS NOT NULL AND fetched < $forget";
            forget.Parameters.AddWithValue("$forget", forgetBeforeMs);
            await forget.ExecuteNonQueryAsync();
            transaction.Commit();
            return removed;
        }
        finally
        {
            _write.Release();
        }
    }

    private static int DayNumber(DateOnly day) => day.DayNumber;

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
}
