using Microsoft.Data.Sqlite;

namespace GwsBusinessSuite.WorldNewsKit;

// How a story was grouped (18-11-26): a line of its history, newest first on the story page.
// Action is merged, split, moved-in, moved-out or judged.
public sealed record GroupingLogEntry(string Id, string EventId, string? OtherId, string Action, string Detail, string Actor, DateTimeOffset At);

public static class GroupingVerdicts
{
    public const string Apart = "apart";
    public const string Same = "same";
    public const string Unsure = "unsure";
    public const string Person = "person";
    public const string Ai = "ai";
}

// Pull latest and Regroup's corrections (18-11-26): pairs of stories judged the same or different
// (a person's verdict outranks the AI's, and "apart" stops every later merge), and each story's
// grouping history. Same database as the stories.
public sealed partial class WorldNewsStore
{
    private const string GroupingSchema = """
        CREATE TABLE IF NOT EXISTS grouping_pairs (
            event_a TEXT NOT NULL, event_b TEXT NOT NULL, verdict TEXT NOT NULL, source TEXT NOT NULL, reason TEXT NOT NULL,
            actor TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY (event_a, event_b));
        CREATE TABLE IF NOT EXISTS grouping_log (
            id TEXT PRIMARY KEY, event_id TEXT NOT NULL, other_id TEXT, action TEXT NOT NULL, detail TEXT NOT NULL,
            actor TEXT NOT NULL, at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_grouping_log_event ON grouping_log (event_id, at);
        """;

    public IReadOnlySet<(string, string)> ApartPairs() => Pairs("WHERE verdict = 'apart'");

    // Every pair anyone has judged, so Regroup doesn't ask the AI twice.
    public IReadOnlySet<(string, string)> JudgedPairs() => Pairs("");

    private HashSet<(string, string)> Pairs(string where)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT event_a, event_b FROM grouping_pairs {where}";
        using var reader = command.ExecuteReader();
        var pairs = new HashSet<(string, string)>();
        while (reader.Read()) pairs.Add(GroupingPairs.Key(reader.GetString(0), reader.GetString(1)));
        return pairs;
    }

    // A person's verdict replaces any earlier one; the AI's never replaces a person's.
    public void SavePairVerdict(string eventA, string eventB, string verdict, string source, string reason, string actor, DateTimeOffset at)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SavePairVerdict(connection, eventA, eventB, verdict, source, reason, actor, at);
        transaction.Commit();
    }

    private static void SavePairVerdict(SqliteConnection connection, string eventA, string eventB, string verdict, string source,
        string reason, string actor, DateTimeOffset at)
    {
        var (a, b) = GroupingPairs.Key(eventA, eventB);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM grouping_pairs WHERE ((event_a = $a AND event_b = $b) OR (event_a = $b AND event_b = $a)) AND source = 'person';
            """;
        command.Parameters.AddWithValue("$a", a);
        command.Parameters.AddWithValue("$b", b);
        if (source != GroupingVerdicts.Person && (long)command.ExecuteScalar()! > 0) return;
        command.CommandText = """
            DELETE FROM grouping_pairs WHERE (event_a = $a AND event_b = $b) OR (event_a = $b AND event_b = $a);
            INSERT INTO grouping_pairs (event_a, event_b, verdict, source, reason, actor, at) VALUES ($a, $b, $verdict, $source, $reason, $actor, $at);
            """;
        command.Parameters.AddWithValue("$verdict", verdict);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$at", Unix(at));
        command.ExecuteNonQuery();
    }

    public void LogGrouping(string eventId, string? otherId, string action, string detail, string actor, DateTimeOffset at)
    {
        using var connection = Open();
        LogGrouping(connection, eventId, otherId, action, detail, actor, at);
    }

    private static void LogGrouping(SqliteConnection connection, string eventId, string? otherId, string action, string detail, string actor, DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO grouping_log (id, event_id, other_id, action, detail, actor, at) VALUES ($id, $event, $other, $action, $detail, $actor, $at)
            """;
        command.Parameters.AddWithValue("$id", NewId());
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$other", (object?)otherId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$at", Unix(at));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<GroupingLogEntry> GroupingHistory(string eventId, int limit = 50)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, event_id, other_id, action, detail, actor, at FROM grouping_log WHERE event_id = $event
            ORDER BY at DESC, rowid DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<GroupingLogEntry>();
        while (reader.Read())
            entries.Add(new GroupingLogEntry(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), At(reader, 6)));
        return entries;
    }

    public NewsEvent? GroupedEvent(string eventId) => ReadEvents("WHERE id = $id", ("$id", eventId)).FirstOrDefault();

    // "Not the same story": the chosen reports leave for a new story, and the two are kept apart
    // from then on. vectors: the reports' headline embeddings (unit length); without them the new
    // story takes its share of the old one's vector. Returns the new story's id, or null when the
    // story is gone or every report (or none) was chosen.
    public string? SplitReports(string eventId, IReadOnlyCollection<string> articleIds, IReadOnlyDictionary<string, float[]>? vectors,
        string actor, DateTimeOffset at)
    {
        var source = GroupedEvent(eventId);
        if (source is null) return null;
        var reports = ArticlesForEvent(eventId);
        var chosen = reports.Where(r => articleIds.Contains(r.Id)).ToList();
        if (chosen.Count == 0 || chosen.Count >= reports.Count) return null;

        var share = VectorShare(source, chosen, reports.Count, vectors);
        var newId = NewId();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO events (id, title, title_language, vector_sum, article_count, first_seen, last_seen)
                VALUES ($id, '', '', $sum, 0, 0, 0)
                """;
            insert.Parameters.AddWithValue("$id", newId);
            insert.Parameters.AddWithValue("$sum", ToBytes(share));
            insert.ExecuteNonQuery();
        }
        MoveRows(connection, chosen, newId);
        SaveSum(connection, eventId, Subtract(source.Sum, share));
        Refresh(connection, eventId);
        Refresh(connection, newId);
        var detail = $"{Reports(chosen.Count)} judged not the same story, moved to “{chosen.OrderByDescending(r => r.SeenAt).First().Title}”";
        SavePairVerdict(connection, eventId, newId, GroupingVerdicts.Apart, GroupingVerdicts.Person, "split by hand", actor, at);
        LogGrouping(connection, eventId, newId, "split", detail, actor, at);
        LogGrouping(connection, newId, eventId, "split", $"Split from “{source.Title}” with {Reports(chosen.Count)}", actor, at);
        transaction.Commit();
        return newId;
    }

    // Moves the chosen reports to another story. False when either story is gone, the chosen
    // reports would leave the first one empty (merge instead), or none were chosen.
    public bool MoveReports(string fromId, string toId, IReadOnlyCollection<string> articleIds, IReadOnlyDictionary<string, float[]>? vectors,
        string actor, DateTimeOffset at)
    {
        if (fromId == toId) return false;
        var from = GroupedEvent(fromId);
        var to = GroupedEvent(toId);
        if (from is null || to is null) return false;
        var reports = ArticlesForEvent(fromId);
        var chosen = reports.Where(r => articleIds.Contains(r.Id)).ToList();
        if (chosen.Count == 0 || chosen.Count >= reports.Count) return false;

        var share = VectorShare(from, chosen, reports.Count, vectors);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        MoveRows(connection, chosen, toId);
        SaveSum(connection, fromId, Subtract(from.Sum, share));
        SaveSum(connection, toId, to.Sum.Length == share.Length ? Add(to.Sum, share) : to.Sum);
        Refresh(connection, fromId);
        Refresh(connection, toId);
        LogGrouping(connection, fromId, toId, "moved-out", $"{Reports(chosen.Count)} moved to “{to.Title}”", actor, at);
        LogGrouping(connection, toId, fromId, "moved-in", $"{Reports(chosen.Count)} moved in from “{from.Title}”", actor, at);
        transaction.Commit();
        return true;
    }

    // Two stories a person says are one: the larger keeps its id. Returns the kept id, or null when
    // either is gone.
    public string? MergeStories(string eventA, string eventB, string actor, DateTimeOffset at)
    {
        if (eventA == eventB) return null;
        var a = GroupedEvent(eventA);
        var b = GroupedEvent(eventB);
        if (a is null || b is null) return null;
        var (keep, drop) = a.ArticleCount >= b.ArticleCount ? (a, b) : (b, a);
        var similarity = (double)EventClusterer.Dot(a.Centroid, b.Centroid);
        if (keep.Sum.Length == drop.Sum.Length) keep.Absorb(drop);
        else
        {
            keep.ArticleCount += drop.ArticleCount;
            if (drop.FirstSeen < keep.FirstSeen) keep.FirstSeen = drop.FirstSeen;
            if (drop.LastSeen > keep.LastSeen) keep.LastSeen = drop.LastSeen;
        }
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM grouping_pairs WHERE (event_a = $a AND event_b = $b) OR (event_a = $b AND event_b = $a)";
            command.Parameters.AddWithValue("$a", eventA);
            command.Parameters.AddWithValue("$b", eventB);
            command.ExecuteNonQuery();
        }
        ApplyMerges([new EventMerge(keep, drop, similarity, "merged by hand; " + GroupingRules.Explain(similarity, keep.Title, drop.Title))], actor, at);
        return keep.Id;
    }

    // Inside ApplyMerges' transaction: the dropped story's verdicts and history become the kept one's.
    private static void CarryGrouping(SqliteConnection connection, EventMerge merge, string actor, DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OR IGNORE grouping_pairs SET event_a = $keep WHERE event_a = $drop;
            UPDATE OR IGNORE grouping_pairs SET event_b = $keep WHERE event_b = $drop;
            DELETE FROM grouping_pairs WHERE event_a = $drop OR event_b = $drop OR event_a = event_b;
            UPDATE grouping_log SET event_id = $keep WHERE event_id = $drop;
            """;
        command.Parameters.AddWithValue("$keep", merge.Keep.Id);
        command.Parameters.AddWithValue("$drop", merge.Drop.Id);
        command.ExecuteNonQuery();
        var detail = $"Merged “{merge.Drop.Title}” ({Reports(merge.Drop.ArticleCount)})" + (merge.Reason.Length == 0 ? "" : $": {merge.Reason}");
        LogGrouping(connection, merge.Keep.Id, merge.Drop.Id, "merged", detail, actor, at);
    }

    private static void MoveRows(SqliteConnection connection, IReadOnlyList<WorldArticle> reports, string toId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET event_id = $event WHERE id = $id";
        var id = command.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
        command.Parameters.AddWithValue("$event", toId);
        foreach (var report in reports)
        {
            id.Value = report.Id;
            command.ExecuteNonQuery();
        }
    }

    private static void SaveSum(SqliteConnection connection, string eventId, float[] sum)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE events SET vector_sum = $sum WHERE id = $id";
        command.Parameters.AddWithValue("$sum", ToBytes(sum));
        command.Parameters.AddWithValue("$id", eventId);
        command.ExecuteNonQuery();
    }

    // A story's count, times, headline (the newest English one, else the newest) and English
    // report, from the reports it holds now. Its summaries and analyses no longer describe them,
    // so they go and are written again.
    private static void Refresh(SqliteConnection connection, string eventId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE events SET
                article_count = (SELECT COUNT(*) FROM articles WHERE event_id = $id),
                first_seen = COALESCE((SELECT MIN(seen_at) FROM articles WHERE event_id = $id), first_seen),
                last_seen = COALESCE((SELECT MAX(seen_at) FROM articles WHERE event_id = $id), last_seen),
                title = COALESCE((SELECT title FROM articles WHERE event_id = $id
                                  ORDER BY language = 'English' DESC, seen_at DESC, id LIMIT 1), title),
                title_language = COALESCE((SELECT language FROM articles WHERE event_id = $id
                                  ORDER BY language = 'English' DESC, seen_at DESC, id LIMIT 1), title_language),
                english_article_id = (SELECT a.id FROM articles a JOIN article_translations t ON t.article_id = a.id
                                      WHERE a.event_id = $id AND t.english <> ''
                                        AND (t.source_title IS NULL OR (t.source_title = a.title
                                             AND COALESCE(t.source_snippet, '') = COALESCE(a.snippet, '')))
                                      ORDER BY a.seen_at DESC, a.id LIMIT 1)
            WHERE id = $id;
            DELETE FROM summaries WHERE event_id = $id;
            DELETE FROM analyses WHERE event_id = $id;
            """;
        command.Parameters.AddWithValue("$id", eventId);
        command.ExecuteNonQuery();
    }

    // The chosen reports' part of a story's vector: their own unit vectors when every one is known
    // and the right length, else their share of the story's.
    private static float[] VectorShare(NewsEvent story, IReadOnlyList<WorldArticle> chosen, int total, IReadOnlyDictionary<string, float[]>? vectors)
    {
        if (vectors is not null && chosen.All(r => vectors.TryGetValue(r.Id, out var v) && v.Length == story.Sum.Length))
        {
            var sum = new float[story.Sum.Length];
            foreach (var report in chosen)
            {
                var unit = EventClusterer.Normalize(vectors[report.Id]);
                for (var i = 0; i < sum.Length; i++) sum[i] += unit[i];
            }
            return sum;
        }
        var fraction = (float)chosen.Count / total;
        return story.Sum.Select(x => x * fraction).ToArray();
    }

    private static float[] Subtract(float[] a, float[] b) => a.Length == b.Length ? a.Select((x, i) => x - b[i]).ToArray() : a;
    private static float[] Add(float[] a, float[] b) => a.Select((x, i) => x + b[i]).ToArray();
    private static string Reports(int count) => count == 1 ? "1 report" : $"{count} reports";
}
