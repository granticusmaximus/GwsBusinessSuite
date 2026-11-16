using Microsoft.Data.Sqlite;

namespace GwsBusinessSuite.WorldNewsKit;

public sealed partial class WorldNewsStore
{
    private static void EnsureEnglishSchema(SqliteConnection connection)
    {
        foreach (var (name, type) in new[]
        {
            ("source_title", "TEXT"), ("source_snippet", "TEXT"), ("description", "TEXT"),
            ("summary", "TEXT"), ("retry_after", "INTEGER NOT NULL DEFAULT 0")
        })
        {
            using var column = connection.CreateCommand();
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('article_translations') WHERE name = $name";
            column.Parameters.AddWithValue("$name", name);
            if ((long)column.ExecuteScalar()! == 0)
                Execute(connection, $"ALTER TABLE article_translations ADD COLUMN {name} {type}");
        }
        using (var column = connection.CreateCommand())
        {
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('articles') WHERE name = 'english_pending'";
            if ((long)column.ExecuteScalar()! == 0)
                Execute(connection, """
                    ALTER TABLE articles ADD COLUMN english_pending INTEGER NOT NULL DEFAULT 1;
                    UPDATE articles SET english_pending = 0 WHERE id IN (
                        SELECT t.article_id FROM article_translations t JOIN articles a ON a.id = t.article_id
                        WHERE t.summary IS NOT NULL AND t.english <> '' AND t.source_title = a.title
                          AND COALESCE(t.source_snippet, '') = COALESCE(a.snippet, ''));
                    """);
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('events') WHERE name = 'english_article_id'";
            if ((long)column.ExecuteScalar()! == 0)
                Execute(connection, "ALTER TABLE events ADD COLUMN english_article_id TEXT");
        }
        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS article_english_changed AFTER UPDATE OF title, snippet ON articles
                WHEN old.title IS NOT new.title OR old.snippet IS NOT new.snippet
                BEGIN
                    UPDATE articles SET english_pending = 1 WHERE id = new.id;
                    UPDATE article_translations SET english = '' WHERE article_id = new.id AND source_title IS NULL;
                END;
            CREATE VIRTUAL TABLE IF NOT EXISTS article_english_fts USING fts5(
                english, description, summary, content='article_translations', content_rowid='rowid',
                tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS article_english_insert AFTER INSERT ON article_translations BEGIN
                INSERT INTO article_english_fts(rowid, english, description, summary)
                    VALUES(new.rowid, new.english, new.description, new.summary); END;
            CREATE TRIGGER IF NOT EXISTS article_english_delete AFTER DELETE ON article_translations BEGIN
                INSERT INTO article_english_fts(article_english_fts, rowid, english, description, summary)
                    VALUES('delete', old.rowid, old.english, old.description, old.summary); END;
            CREATE TRIGGER IF NOT EXISTS article_english_update AFTER UPDATE OF english, description, summary ON article_translations BEGIN
                INSERT INTO article_english_fts(article_english_fts, rowid, english, description, summary)
                    VALUES('delete', old.rowid, old.english, old.description, old.summary);
                INSERT INTO article_english_fts(rowid, english, description, summary)
                    VALUES(new.rowid, new.english, new.description, new.summary); END;
            CREATE TRIGGER IF NOT EXISTS article_english_grouped AFTER UPDATE OF event_id ON articles
                WHEN new.event_id IS NOT NULL AND EXISTS (
                    SELECT 1 FROM article_translations t WHERE t.article_id = new.id AND t.english <> ''
                      AND (t.source_title IS NULL OR (t.source_title = new.title
                           AND COALESCE(t.source_snippet, '') = COALESCE(new.snippet, ''))))
                BEGIN
                    UPDATE events SET english_article_id = new.id WHERE id = new.event_id
                      AND (english_article_id IS NULL OR
                           COALESCE((SELECT seen_at FROM articles WHERE id = english_article_id), -1) <= new.seen_at);
                END;
            """);
    }

    // Both indexes have their own external-content table; original article text stays untouched.
    private static bool EnsureEnglishFullText(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM store_flags WHERE name = 'english_fts_built' AND value = 1";
        if ((long)command.ExecuteScalar()! != 0) return false;
        Execute(connection, """
            INSERT INTO article_english_fts(article_english_fts) VALUES('rebuild');
            UPDATE articles SET english_pending = 0 WHERE id IN (
                SELECT t.article_id FROM article_translations t JOIN articles a ON a.id = t.article_id
                WHERE t.summary IS NOT NULL AND t.english <> '' AND t.source_title = a.title
                  AND COALESCE(t.source_snippet, '') = COALESCE(a.snippet, ''));
            UPDATE events SET english_article_id = (
                SELECT a.id FROM articles a JOIN article_translations t ON t.article_id = a.id
                WHERE a.event_id = events.id AND t.english <> ''
                ORDER BY a.seen_at DESC, a.id LIMIT 1)
            WHERE id IN (SELECT a.event_id FROM article_translations t JOIN articles a ON a.id = t.article_id WHERE t.english <> '');
            INSERT OR REPLACE INTO store_flags(name, value) VALUES('english_fts_built', 1);
            """);
        return true;
    }

    private const string CurrentEnglish = """
        t.source_title = a.title AND COALESCE(t.source_snippet, '') = COALESCE(a.snippet, '')
        """;

    public void EnsureEnglishQueueIndex()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_articles_english_pending'";
        if ((long)command.ExecuteScalar()! != 0) return;
        command.CommandTimeout = 0;
        command.CommandText = "CREATE INDEX IF NOT EXISTS ix_articles_english_pending ON articles(seen_at, id) WHERE english_pending = 1";
        command.ExecuteNonQuery();
    }

    private const string EnglishEventHeadline = """
        COALESCE(
            (SELECT headline FROM summaries WHERE event_id = e.id AND NULLIF(headline, '') IS NOT NULL ORDER BY version DESC LIMIT 1),
            (SELECT t.english FROM articles a JOIN article_translations t ON t.article_id = a.id
             WHERE a.id = e.english_article_id AND a.event_id = e.id
               AND (t.source_title IS NULL OR (t.source_title = a.title
                    AND COALESCE(t.source_snippet, '') = COALESCE(a.snippet, ''))) AND t.english <> ''),
            CASE WHEN lower(e.title_language) = 'english' THEN NULL ELSE 'English translation pending' END)
        """;

    public ReportTranslation? LatestEnglishReport(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.article_id, t.source_title, t.source_snippet, t.english, t.description, t.summary
            FROM article_translations t JOIN articles a ON a.id = t.article_id
            WHERE a.event_id = $event AND {CurrentEnglish} AND t.summary IS NOT NULL
            ORDER BY a.seen_at DESC, a.id LIMIT 1
            """;
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEnglishReport(reader) : null;
    }

    public IReadOnlyList<WorldArticle> ReportsDueEnglish(DateTimeOffset now, int limit, bool oldestFirst = false) =>
        QueryArticles($"""
            WHERE english_pending = 1
              AND NOT EXISTS (SELECT 1 FROM article_translations t
                              WHERE t.article_id = articles.id AND t.retry_after > $now)
            ORDER BY seen_at {(oldestFirst ? "ASC" : "DESC")}, id LIMIT $limit
            """, ("$now", now.ToUnixTimeSeconds()), ("$limit", Math.Clamp(limit, 1, HeadlineTranslator.ReportsPerCall)));

    public void DeferEnglishReports(IEnumerable<string> articleIds, DateTimeOffset retryAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in articleIds.Distinct(StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO article_translations(article_id, english, created_at, retry_after)
                SELECT id, '', 0, $retry FROM articles WHERE id = $id
                ON CONFLICT(article_id) DO UPDATE SET retry_after = excluded.retry_after
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$retry", retryAt.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public int SaveEnglishReports(IReadOnlyList<ReportTranslation> translations, DateTimeOffset at)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var saved = 0;
        foreach (var translation in translations)
        {
            if (string.IsNullOrWhiteSpace(translation.Headline) || string.IsNullOrWhiteSpace(translation.Summary) ||
                (!string.IsNullOrWhiteSpace(translation.SourceSnippet) && string.IsNullOrWhiteSpace(translation.Description)))
                throw new ArgumentException("An English report requires a headline, summary and any supplied feed description.", nameof(translations));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO article_translations(article_id, english, created_at, source_title, source_snippet, description, summary, retry_after)
                SELECT id, $english, $at, title, snippet, $description, $summary, 0 FROM articles
                WHERE id = $id AND title = $sourceTitle AND COALESCE(snippet, '') = COALESCE($sourceSnippet, '')
                ON CONFLICT(article_id) DO UPDATE SET
                    english = excluded.english, created_at = excluded.created_at, source_title = excluded.source_title,
                    source_snippet = excluded.source_snippet, description = excluded.description,
                    summary = excluded.summary, retry_after = 0
                """;
            command.Parameters.AddWithValue("$id", translation.ArticleId);
            command.Parameters.AddWithValue("$sourceTitle", translation.SourceTitle);
            command.Parameters.AddWithValue("$sourceSnippet", (object?)translation.SourceSnippet ?? DBNull.Value);
            command.Parameters.AddWithValue("$english", translation.Headline);
            command.Parameters.AddWithValue("$description", (object?)translation.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary", translation.Summary);
            command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            var written = command.ExecuteNonQuery();
            saved += written;
            if (written > 0) UpdateEnglishEvent(connection, transaction, translation.ArticleId);
        }
        transaction.Commit();
        return saved;
    }

    private static void UpdateEnglishEvent(SqliteConnection connection, SqliteTransaction transaction, string articleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE articles SET english_pending = CASE WHEN EXISTS (
                SELECT 1 FROM article_translations t WHERE t.article_id = articles.id
                  AND t.summary IS NOT NULL AND t.english <> '' AND t.source_title = articles.title
                  AND COALESCE(t.source_snippet, '') = COALESCE(articles.snippet, '')) THEN 0 ELSE 1 END
            WHERE id = $id;
            UPDATE events SET english_article_id = $id
            WHERE id = (SELECT event_id FROM articles WHERE id = $id)
              AND (english_article_id IS NULL OR
                   COALESCE((SELECT seen_at FROM articles WHERE id = english_article_id), -1)
                       <= (SELECT seen_at FROM articles WHERE id = $id))
            """;
        command.Parameters.AddWithValue("$id", articleId);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<WorldArticle> WithEnglish(IReadOnlyList<WorldArticle> articles)
    {
        if (articles.Count == 0) return articles;
        var translations = new Dictionary<string, ReportTranslation>(StringComparer.Ordinal);
        using var connection = Open();
        foreach (var batch in articles.Select(article => article.Id).Distinct(StringComparer.Ordinal).Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT t.article_id, COALESCE(t.source_title, a.title),
                    CASE WHEN t.source_title IS NULL THEN a.snippet ELSE t.source_snippet END,
                    t.english, t.description, t.summary
                FROM article_translations t JOIN articles a ON a.id = t.article_id
                WHERE (t.source_title IS NULL OR ({CurrentEnglish})) AND t.english <> ''
                  AND t.article_id IN ({string.Join(',', batch.Select((_, i) => $"$id{i}"))})
                """;
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$id{i}", batch[i]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                translations.Add(reader.GetString(0), ReadEnglishReport(reader));
        }

        return articles.Select(article => translations.TryGetValue(article.Id, out var translation) &&
            translation.SourceTitle == article.Title && (translation.SourceSnippet ?? "") == (article.Snippet ?? "")
            ? article with { English = translation } : article).ToList();
    }

    private static ReportTranslation ReadEnglishReport(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static string EnglishTextMatch(string articleAlias, string parameter, bool fullText) =>
        $"""
        {articleAlias}.id IN (SELECT t.article_id FROM article_translations t JOIN articles a ON a.id = t.article_id
                WHERE (t.source_title IS NULL OR ({CurrentEnglish})) AND
        """ + (fullText
            ? $" t.rowid IN (SELECT rowid FROM article_english_fts WHERE article_english_fts MATCH {parameter}))"
            : $" instr(lower(t.english || ' ' || COALESCE(t.description, '') || ' ' || COALESCE(t.summary, '')), lower({parameter})) > 0)");
}
