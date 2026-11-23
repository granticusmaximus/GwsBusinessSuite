using Microsoft.Data.Sqlite;

namespace GwsBusinessSuite.WorldNewsKit;

// The desk's records (N5-N15, 14-11-26): beats, notes, the source book, sign-offs, live updates,
// holds, corrections, dossiers and the budget's history. Same database as the stories.
public sealed partial class WorldNewsStore
{
    private const string DeskSchema = """
        CREATE TABLE IF NOT EXISTS beats (id TEXT PRIMARY KEY, username TEXT NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL, created_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS beat_hits (beat_id TEXT NOT NULL, event_id TEXT NOT NULL, PRIMARY KEY (beat_id, event_id));
        CREATE TABLE IF NOT EXISTS desk_notes (id TEXT PRIMARY KEY, event_id TEXT NOT NULL, username TEXT NOT NULL, text TEXT NOT NULL, created_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_desk_notes_event ON desk_notes (event_id, created_at);
        CREATE TABLE IF NOT EXISTS source_contacts (
            id TEXT PRIMARY KEY, owner TEXT NOT NULL, name TEXT NOT NULL, organisation TEXT NOT NULL, contact TEXT NOT NULL,
            reliability INTEGER NOT NULL, notes TEXT NOT NULL, created_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_source_contacts_owner ON source_contacts (owner, name);
        CREATE TABLE IF NOT EXISTS source_uses (source_id TEXT NOT NULL, event_id TEXT NOT NULL, outcome TEXT NOT NULL, note TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY (source_id, event_id));
        CREATE TABLE IF NOT EXISTS sign_offs (
            id INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL, kind TEXT NOT NULL, username TEXT NOT NULL, label_level TEXT NOT NULL,
            at INTEGER NOT NULL, previous_hash TEXT NOT NULL, hash TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_sign_offs_event ON sign_offs (event_id, id);
        CREATE TABLE IF NOT EXISTS live_updates (
            id TEXT PRIMARY KEY, event_id TEXT NOT NULL, username TEXT NOT NULL, text TEXT NOT NULL, status TEXT NOT NULL, created_at INTEGER NOT NULL,
            corrected_text TEXT, corrected_at INTEGER);
        CREATE INDEX IF NOT EXISTS ix_live_updates_event ON live_updates (event_id, created_at);
        CREATE TABLE IF NOT EXISTS holds (event_id TEXT PRIMARY KEY, until INTEGER NOT NULL, reason TEXT NOT NULL, set_by TEXT NOT NULL, set_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS corrections (id TEXT PRIMARY KEY, event_id TEXT NOT NULL, title TEXT NOT NULL, text TEXT NOT NULL, username TEXT NOT NULL, at INTEGER NOT NULL, article_id TEXT);
        CREATE TABLE IF NOT EXISTS dossier_items (
            id TEXT PRIMARY KEY, event_id TEXT NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, url TEXT, detail TEXT NOT NULL, added_by TEXT NOT NULL, at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_dossier_items_event ON dossier_items (event_id, at);
        CREATE TABLE IF NOT EXISTS budget_history (event_id TEXT NOT NULL, status TEXT NOT NULL, at INTEGER NOT NULL, by TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_budget_history_event ON budget_history (event_id, at);
        """;

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static long Unix(DateTimeOffset at) => at.ToUnixTimeSeconds();
    private static DateTimeOffset At(SqliteDataReader reader, int i) => DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(i));

    private static void EnsureDeskSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('corrections') WHERE name = 'article_id'";
        if ((long)command.ExecuteScalar()! == 0) Execute(connection, "ALTER TABLE corrections ADD COLUMN article_id TEXT");
        Execute(connection, """
            CREATE INDEX IF NOT EXISTS ix_corrections_at ON corrections (at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_corrections_event ON corrections (event_id, at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_corrections_article ON corrections (article_id, at DESC, id DESC);
            """);
    }

    // ---- N5 beats ----

    public IReadOnlyList<Beat> Beats(string? username = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, kind, value, created_at FROM beats" + (username is null ? "" : " WHERE username = $user") + " ORDER BY kind, value";
        if (username is not null) command.Parameters.AddWithValue("$user", NormalizeUsername(username));
        using var reader = command.ExecuteReader();
        var beats = new List<Beat>();
        while (reader.Read()) beats.Add(new Beat(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), At(reader, 4)));
        return beats;
    }

    public Beat? AddBeat(string username, string kind, string value, DateTimeOffset now)
    {
        if (BeatRules.Problem(kind, value) is { } problem) throw new ArgumentException(problem, nameof(value));
        var mine = Beats(username);
        if (mine.Count >= BeatRules.MaxPerPerson || mine.Any(b => b.Kind == kind && string.Equals(b.Value, value, StringComparison.OrdinalIgnoreCase))) return null;
        var beat = new Beat(NewId(), NormalizeUsername(username), kind, value, now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO beats (id, username, kind, value, created_at) VALUES ($id, $user, $kind, $value, $at)";
        command.Parameters.AddWithValue("$id", beat.Id);
        command.Parameters.AddWithValue("$user", beat.Username);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
        return beat;
    }

    public void RemoveBeat(string username, string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM beat_hits WHERE beat_id = $id AND EXISTS (SELECT 1 FROM beats WHERE id = $id AND username = $user);
            DELETE FROM beats WHERE id = $id AND username = $user;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", NormalizeUsername(username));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // The stories on a person's beats since a time, most covered first: their queue.
    public IReadOnlyList<EventOverview> BeatQueue(string username, DateTimeOffset since, int limit = 40)
    {
        var beats = Beats(username);
        if (beats.Count == 0) return [];
        var events = TopEvents(since, 1000);
        var kinds = EventKinds(events.Select(e => e.Id));
        return events.Where(e => beats.Any(beat => BeatRules.Matches(beat, e.Countries, kinds.GetValueOrDefault(e.Id)))).Take(limit).ToList();
    }

    // One bell entry per new story on a beat (once per story and beat), the most covered first,
    // at most MaxAlertsPerRead per person per read. Held stories send none.
    public IReadOnlyList<WorldNewsAlert> CreateBeatAlerts(DateTimeOffset now, DateTimeOffset since, Func<string, bool>? mayAlert = null)
    {
        var beats = Beats();
        if (beats.Count == 0) return [];
        var events = TopEvents(since, 500);
        var kinds = EventKinds(events.Select(e => e.Id));
        var held = Holds().Where(h => global::GwsBusinessSuite.WorldNewsKit.Holds.IsHeld(h.Value, now)).Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var alerts = new List<WorldNewsAlert>();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var person in beats.GroupBy(b => b.Username, StringComparer.OrdinalIgnoreCase))
        {
            var sent = 0;
            foreach (var e in events)
            {
                if (sent >= BeatRules.MaxAlertsPerRead) break;
                if (held.Contains(e.Id)) continue;
                var beat = person.FirstOrDefault(b => BeatRules.Matches(b, e.Countries, kinds.GetValueOrDefault(e.Id)));
                if (beat is null) continue;
                using var seen = connection.CreateCommand();
                seen.CommandText = "SELECT 1 FROM beat_hits WHERE beat_id = $beat AND event_id = $event";
                seen.Parameters.AddWithValue("$beat", beat.Id);
                seen.Parameters.AddWithValue("$event", e.Id);
                if (seen.ExecuteScalar() is not null) continue;
                var situationId = SituationOf(e.Id);
                if (situationId is null || (mayAlert is not null && !mayAlert(situationId))) continue;
                using var hit = connection.CreateCommand();
                hit.CommandText = "INSERT OR IGNORE INTO beat_hits (beat_id, event_id) VALUES ($beat, $event)";
                hit.Parameters.AddWithValue("$beat", beat.Id);
                hit.Parameters.AddWithValue("$event", e.Id);
                hit.ExecuteNonQuery();
                var alert = new WorldNewsAlert(Guid.NewGuid(), person.Key, situationId, e.Headline ?? e.Title, $"On your beat: {BeatRules.Label(beat)}", now, false);
                InsertAlert(connection, alert);
                alerts.Add(alert);
                sent++;
            }
        }
        transaction.Commit();
        return alerts;
    }

    private static void InsertAlert(SqliteConnection connection, WorldNewsAlert alert)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO world_news_alerts (id, username, situation_id, title, message, created_at) VALUES ($id, $user, $situation, $title, $message, $created)";
        insert.Parameters.AddWithValue("$id", alert.Id.ToString("N"));
        insert.Parameters.AddWithValue("$user", alert.Username);
        insert.Parameters.AddWithValue("$situation", alert.SituationId);
        insert.Parameters.AddWithValue("$title", alert.Title);
        insert.Parameters.AddWithValue("$message", alert.Message);
        insert.Parameters.AddWithValue("$created", Unix(alert.CreatedAt));
        insert.ExecuteNonQuery();
    }

    // ---- N6 shift handover ----

    public IReadOnlyDictionary<string, string> BudgetLabelLevels()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.event_id, (SELECT level FROM label_snapshots WHERE event_id = b.event_id ORDER BY id DESC LIMIT 1)
            FROM budget b
            """;
        using var reader = command.ExecuteReader();
        var levels = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            if (!reader.IsDBNull(1)) levels[reader.GetString(0)] = reader.GetString(1);
        return levels;
    }

    // ---- N7 desk notes ----

    public IReadOnlyList<DeskNote> DeskNotes(string? eventId = null, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, username, text, created_at FROM desk_notes" + (eventId is null ? "" : " WHERE event_id = $event") + " ORDER BY created_at DESC, rowid DESC LIMIT $limit";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var notes = new List<DeskNote>();
        while (reader.Read()) notes.Add(new DeskNote(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), At(reader, 4)));
        return notes;
    }

    // Keeps the note and, for each @mention, a bell entry for that person (the story's situation,
    // or the event id when it isn't in one yet). Returns the note and the alerts to raise.
    public (DeskNote Note, IReadOnlyList<WorldNewsAlert> Alerts) AddDeskNote(string eventId, string username, string text, DateTimeOffset now, Func<string, bool>? knownUser = null)
    {
        if (global::GwsBusinessSuite.WorldNewsKit.DeskNotes.Problem(text) is { } problem) throw new ArgumentException(problem, nameof(text));
        text = text.Trim();
        var note = new DeskNote(NewId(), eventId, username, text, now);
        var title = Event(eventId) is { } e ? e.Headline ?? e.Title : "A story";
        var situationId = SituationOf(eventId) ?? eventId;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO desk_notes (id, event_id, username, text, created_at) VALUES ($id, $event, $user, $text, $at)";
            command.Parameters.AddWithValue("$id", note.Id);
            command.Parameters.AddWithValue("$event", eventId);
            command.Parameters.AddWithValue("$user", username);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$at", Unix(now));
            command.ExecuteNonQuery();
        }
        var alerts = new List<WorldNewsAlert>();
        foreach (var mentioned in global::GwsBusinessSuite.WorldNewsKit.DeskNotes.Mentions(text))
        {
            if (string.Equals(mentioned, username, StringComparison.OrdinalIgnoreCase) || (knownUser is not null && !knownUser(mentioned))) continue;
            var alert = new WorldNewsAlert(Guid.NewGuid(), NormalizeUsername(mentioned), situationId, title,
                $"{username} mentioned you on the desk: {(text.Length > 140 ? text[..140] + "…" : text)}", now, false);
            InsertAlert(connection, alert);
            alerts.Add(alert);
        }
        transaction.Commit();
        return (note, alerts);
    }

    // ---- N8 source book (private to its owner) ----

    public IReadOnlyList<SourceContact> SourceContacts(string owner, int offset = 0, int? limit = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, owner, name, organisation, contact, reliability, notes, created_at FROM source_contacts WHERE owner = $owner ORDER BY name, id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit ?? -1);
        using var reader = command.ExecuteReader();
        var contacts = new List<SourceContact>();
        while (reader.Read())
            contacts.Add(new SourceContact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6), At(reader, 7)));
        return contacts;
    }

    public SourceContact? FindSourceContact(string owner, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, owner, name, organisation, contact, reliability, notes, created_at FROM source_contacts WHERE owner = $owner AND id = $id";
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SourceContact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6), At(reader, 7))
            : null;
    }

    public SourceContact SaveSourceContact(SourceContact contact)
    {
        if (SourceBook.Problem(contact.Name, contact.Reliability) is { } problem) throw new ArgumentException(problem, nameof(contact));
        var saved = contact with
        {
            Owner = NormalizeUsername(contact.Owner), Id = string.IsNullOrEmpty(contact.Id) ? NewId() : contact.Id,
            Name = contact.Name.Trim(), Organisation = contact.Organisation.Trim(), Contact = contact.Contact.Trim(), Notes = contact.Notes.Trim(),
        };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_contacts (id, owner, name, organisation, contact, reliability, notes, created_at) VALUES ($id, $owner, $name, $org, $contact, $rel, $notes, $at)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, organisation = excluded.organisation, contact = excluded.contact, reliability = excluded.reliability, notes = excluded.notes
            WHERE source_contacts.owner = excluded.owner
            RETURNING created_at
            """;
        command.Parameters.AddWithValue("$id", saved.Id);
        command.Parameters.AddWithValue("$owner", saved.Owner);
        command.Parameters.AddWithValue("$name", saved.Name);
        command.Parameters.AddWithValue("$org", saved.Organisation);
        command.Parameters.AddWithValue("$contact", saved.Contact);
        command.Parameters.AddWithValue("$rel", saved.Reliability);
        command.Parameters.AddWithValue("$notes", saved.Notes);
        command.Parameters.AddWithValue("$at", Unix(saved.CreatedAt));
        if (command.ExecuteScalar() is not long createdAt)
            throw new InvalidOperationException("That contact is no longer available in your source book.");
        return saved with { CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt) };
    }

    public void RemoveSourceContact(string owner, string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM source_uses WHERE source_id = $id AND EXISTS (SELECT 1 FROM source_contacts WHERE id = $id AND owner = $owner); DELETE FROM source_contacts WHERE id = $id AND owner = $owner";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("That contact is no longer available in your source book.");
        transaction.Commit();
    }

    // Only the owner's contacts can be used; a use by anyone else is ignored.
    public bool RecordSourceUse(string owner, string sourceId, string eventId, string outcome, string note, DateTimeOffset now)
    {
        if (!SourceBook.Outcomes.Contains(outcome)) return false;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_uses (source_id, event_id, outcome, note, at)
            SELECT $source, $event, $outcome, $note, $at FROM source_contacts WHERE id = $source AND owner = $owner
            ON CONFLICT(source_id, event_id) DO UPDATE SET outcome = excluded.outcome, note = excluded.note, at = excluded.at
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$note", note.Trim());
        command.Parameters.AddWithValue("$at", Unix(now));
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        return command.ExecuteNonQuery() > 0;
    }

    // The owner's uses: all of them, or those on one story.
    public IReadOnlyList<SourceUse> SourceUses(string owner, string? eventId = null, string? sourceId = null, int offset = 0, int? limit = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.source_id, u.event_id, u.outcome, u.note, u.at FROM source_uses u JOIN source_contacts c ON c.id = u.source_id WHERE c.owner = $owner"
            + (eventId is null ? "" : " AND u.event_id = $event") + (sourceId is null ? "" : " AND u.source_id = $source")
            + " ORDER BY u.at DESC, u.rowid DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        if (sourceId is not null) command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit ?? -1);
        using var reader = command.ExecuteReader();
        var uses = new List<SourceUse>();
        while (reader.Read()) uses.Add(new SourceUse(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), At(reader, 4)));
        return uses;
    }

    public (int Total, int Held, int Wrong) SourceUseCounts(string owner, string sourceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(u.outcome = 'held'), 0), COALESCE(SUM(u.outcome = 'wrong'), 0)
            FROM source_uses u JOIN source_contacts c ON c.id = u.source_id
            WHERE c.owner = $owner AND c.id = $source
            """;
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Source history counts couldn't be read.");
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    // ---- N9 sign-offs ----

    public IReadOnlyList<SignOff> SignOffs(string? eventId = null)
    {
        using var connection = Open();
        return SignOffs(connection, eventId);
    }

    private static IReadOnlyList<SignOff> SignOffs(SqliteConnection connection, string? eventId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, event_id, kind, username, label_level, at, previous_hash, hash FROM sign_offs" + (eventId is null ? "" : " WHERE event_id = $event") + " ORDER BY id";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var list = new List<SignOff>();
        while (reader.Read())
            list.Add(new SignOff(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), At(reader, 5), reader.GetString(6), reader.GetString(7)));
        return list;
    }

    public SignOff AddSignOff(string eventId, string kind, string username, string labelLevel, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        username = username.Trim();
        if (global::GwsBusinessSuite.WorldNewsKit.SignOffs.Problem(SignOffs(connection, eventId, transaction), kind, username,
                DeskChecklist.IsComplete(DeskChecks(connection, eventId, transaction))) is { } problem)
            throw new InvalidOperationException(problem);
        string previous;
        using (var last = connection.CreateCommand())
        {
            last.Transaction = transaction;
            last.CommandText = "SELECT hash FROM sign_offs ORDER BY id DESC LIMIT 1";
            previous = last.ExecuteScalar() as string ?? LabelLedger.Genesis;
        }
        var hash = global::GwsBusinessSuite.WorldNewsKit.SignOffs.HashOf(previous, eventId, kind, username, labelLevel, now);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO sign_offs (event_id, kind, username, label_level, at, previous_hash, hash) VALUES ($event, $kind, $user, $level, $at, $previous, $hash); SELECT last_insert_rowid()";
        insert.Parameters.AddWithValue("$event", eventId);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$user", username);
        insert.Parameters.AddWithValue("$level", labelLevel);
        insert.Parameters.AddWithValue("$at", Unix(now));
        insert.Parameters.AddWithValue("$previous", previous);
        insert.Parameters.AddWithValue("$hash", hash);
        var id = (long)insert.ExecuteScalar()!;
        transaction.Commit();
        return new SignOff(id, eventId, kind, username, labelLevel, now, previous, hash);
    }

    // ---- N10 live updates ----

    public IReadOnlyList<LiveUpdate> LiveUpdates(string? eventId = null, int limit = 200, int offset = 0, bool orderByActivity = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, username, text, status, created_at, corrected_text, corrected_at FROM live_updates"
            + (eventId is null ? "" : " WHERE event_id = $event")
            + (orderByActivity ? " ORDER BY COALESCE(corrected_at, created_at) DESC, rowid DESC" : " ORDER BY created_at DESC, rowid DESC")
            + " LIMIT $limit OFFSET $offset";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var updates = new List<LiveUpdate>();
        while (reader.Read())
            updates.Add(new LiveUpdate(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), At(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : At(reader, 7)));
        return updates;
    }

    public LiveUpdate AddLiveUpdate(string eventId, string username, string text, string status, DateTimeOffset now)
    {
        if (global::GwsBusinessSuite.WorldNewsKit.LiveUpdates.Problem(text, status) is { } problem) throw new ArgumentException(problem, nameof(text));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Sign in before publishing an update.", nameof(username));
        var update = new LiveUpdate(NewId(), eventId, username, text.Trim(), status, now, null, null);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureStoryWritable(connection, transaction, eventId, now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO live_updates (id, event_id, username, text, status, created_at) VALUES ($id, $event, $user, $text, $status, $at)";
        command.Parameters.AddWithValue("$id", update.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$text", update.Text);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
        transaction.Commit();
        return update;
    }

    // A correction keeps the original text and shows the new one in its place.
    public void CorrectLiveUpdate(string id, string correctedText, string status, DateTimeOffset now, string? eventId = null)
    {
        if (global::GwsBusinessSuite.WorldNewsKit.LiveUpdates.Problem(correctedText, status) is { } problem) throw new ArgumentException(problem, nameof(correctedText));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT event_id FROM live_updates WHERE id = $id" + (eventId is null ? "" : " AND event_id = $event");
            find.Parameters.AddWithValue("$id", id);
            if (eventId is not null) find.Parameters.AddWithValue("$event", eventId);
            if (find.ExecuteScalar() is not string savedEventId)
                throw new InvalidOperationException("That live update is no longer available on this story.");
            EnsureStoryWritable(connection, transaction, savedEventId, now);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE live_updates SET corrected_text = $text, corrected_at = $at, status = $status WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$text", correctedText.Trim());
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", Unix(now));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("That live update couldn't be corrected.");
        transaction.Commit();
    }

    private static void EnsureStoryWritable(SqliteConnection connection, SqliteTransaction transaction, string eventId, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT h.until FROM events e LEFT JOIN holds h ON h.event_id = e.id WHERE e.id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("That story is no longer available. Open a current World News story.");
        if (!reader.IsDBNull(0) && At(reader, 0) > now)
            throw new InvalidOperationException("This story is on hold. Wait for its release time or release the hold before publishing.");
    }

    public IReadOnlyList<PublicLiveStory> PublicLiveStories(DateTimeOffset now, int offset = 0, int limit = 50, string? eventId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.id, COALESCE({EnglishEventHeadline}, e.title),
                MAX(COALESCE(u.corrected_at, u.created_at)), COUNT(*)
            FROM live_updates u JOIN events e ON e.id = u.event_id
            WHERE u.status IN ('confirmed', 'unconfirmed')
                AND NOT EXISTS (SELECT 1 FROM holds h WHERE h.event_id = e.id AND h.until > $now)
            """ + (eventId is null ? "" : " AND e.id = $event")
            + " GROUP BY e.id ORDER BY MAX(COALESCE(u.corrected_at, u.created_at)) DESC, e.id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$now", Unix(now));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var stories = new List<PublicLiveStory>();
        while (reader.Read()) stories.Add(new PublicLiveStory(reader.GetString(0), reader.GetString(1), At(reader, 2), reader.GetInt32(3)));
        return stories;
    }

    public IReadOnlyList<PublicLiveUpdate> PublicLiveUpdates(string eventId, DateTimeOffset now, int offset = 0, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.text, u.status, u.created_at, u.corrected_text, u.corrected_at
            FROM live_updates u JOIN events e ON e.id = u.event_id
            WHERE u.event_id = $event AND u.status IN ('confirmed', 'unconfirmed')
                AND NOT EXISTS (SELECT 1 FROM holds h WHERE h.event_id = e.id AND h.until > $now)
            ORDER BY u.created_at DESC, u.rowid DESC LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$now", Unix(now));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var updates = new List<PublicLiveUpdate>();
        while (reader.Read())
            updates.Add(new PublicLiveUpdate(reader.GetString(0), reader.GetString(1), reader.GetString(2), At(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : At(reader, 5)));
        return updates;
    }

    // Stories with at least one live update, newest activity first (for the public live index).
    public IReadOnlyList<(string EventId, DateTimeOffset LastAt, int Updates)> LiveStories(int limit = 50) =>
        PublicLiveStories(DateTimeOffset.UtcNow, limit: limit).Select(story => (story.EventId, story.LastAt, story.Updates)).ToList();

    // ---- N11 holds ----

    public IReadOnlyDictionary<string, StoryHold> Holds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, until, reason, set_by, set_at FROM holds";
        using var reader = command.ExecuteReader();
        var holds = new Dictionary<string, StoryHold>(StringComparer.Ordinal);
        while (reader.Read()) holds[reader.GetString(0)] = new StoryHold(reader.GetString(0), At(reader, 1), reader.GetString(2), reader.GetString(3), At(reader, 4));
        return holds;
    }

    public StoryHold? Hold(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, until, reason, set_by, set_at FROM holds WHERE event_id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new StoryHold(reader.GetString(0), At(reader, 1), reader.GetString(2), reader.GetString(3), At(reader, 4)) : null;
    }

    public void SetHold(StoryHold hold)
    {
        if (global::GwsBusinessSuite.WorldNewsKit.Holds.Problem(hold.Until, hold.Reason, hold.SetAt) is { } problem)
            throw new ArgumentException(problem, nameof(hold));
        if (string.IsNullOrWhiteSpace(hold.SetBy)) throw new ArgumentException("Sign in before setting a hold.", nameof(hold));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO holds (event_id, until, reason, set_by, set_at)
            SELECT id, $until, $reason, $by, $at FROM events WHERE id = $event
            ON CONFLICT(event_id) DO UPDATE SET until = excluded.until, reason = excluded.reason, set_by = excluded.set_by, set_at = excluded.set_at
            """;
        command.Parameters.AddWithValue("$event", hold.EventId);
        command.Parameters.AddWithValue("$until", Unix(hold.Until));
        command.Parameters.AddWithValue("$reason", hold.Reason.Trim());
        command.Parameters.AddWithValue("$by", hold.SetBy);
        command.Parameters.AddWithValue("$at", Unix(hold.SetAt));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("That story is no longer available.");
    }

    public void ReleaseHold(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM holds WHERE event_id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("That hold is no longer available. Refresh its status.");
    }

    // Whether any story in a situation is on hold now (alerts are per situation).
    public bool IsSituationHeld(string situationId, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM events e JOIN holds h ON h.event_id = e.id
                           WHERE e.situation_id = $situation AND h.until > $now)
            """;
        command.Parameters.AddWithValue("$situation", situationId);
        command.Parameters.AddWithValue("$now", Unix(now));
        return (long)command.ExecuteScalar()! != 0;
    }

    // ---- N12 corrections ----

    public IReadOnlyList<Correction> Corrections(string? eventId = null, int limit = 200, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, title, text, username, at, article_id FROM corrections"
            + (eventId is null ? "" : " WHERE event_id = $event") + " ORDER BY at DESC, id DESC LIMIT $limit OFFSET $offset";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var list = new List<Correction>();
        while (reader.Read()) list.Add(new Correction(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), At(reader, 5))
        {
            ArticleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))
        });
        return list;
    }

    public IReadOnlyList<PublicCorrection> PublicCorrections(int offset = 0, int limit = 25, string? eventId = null, string? id = null, Guid? articleId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, title, text, at FROM corrections WHERE 1 = 1"
            + (eventId is null ? "" : " AND event_id = $event")
            + (id is null ? "" : " AND id = $id")
            + (articleId is null ? "" : " AND article_id = $article")
            + " ORDER BY at DESC, id DESC LIMIT $limit OFFSET $offset";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        if (articleId is { } article) command.Parameters.AddWithValue("$article", article.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var list = new List<PublicCorrection>();
        while (reader.Read()) list.Add(new PublicCorrection(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), At(reader, 4)));
        return list;
    }

    public Correction AddCorrection(string eventId, string title, string text, string username, DateTimeOffset now, Guid? articleId = null)
    {
        if (global::GwsBusinessSuite.WorldNewsKit.Corrections.Problem(title, text) is { } problem) throw new ArgumentException(problem, nameof(text));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Sign in before publishing a correction.", nameof(username));
        var correction = new Correction(NewId(), eventId, title.Trim(), text.Trim(), username, now) { ArticleId = articleId };
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureStoryWritable(connection, transaction, eventId, now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO corrections (id, event_id, title, text, username, at, article_id) VALUES ($id, $event, $title, $text, $user, $at, $article)";
        command.Parameters.AddWithValue("$id", correction.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$title", correction.Title);
        command.Parameters.AddWithValue("$text", correction.Text);
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.Parameters.AddWithValue("$article", articleId is { } article ? article.ToString("N") : DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        return correction;
    }

    // ---- N14 dossiers ----

    public IReadOnlyList<DossierItem> Dossier(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, kind, title, url, detail, added_by, at FROM dossier_items WHERE event_id = $event ORDER BY at, id";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var items = new List<DossierItem>();
        while (reader.Read())
            items.Add(new DossierItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), At(reader, 7)));
        return items;
    }

    public DossierItem? PinToDossier(string eventId, string kind, string title, string? url, string detail, string addedBy, DateTimeOffset now)
    {
        if (!global::GwsBusinessSuite.WorldNewsKit.Dossiers.Kinds.Contains(kind)) return null;
        if (global::GwsBusinessSuite.WorldNewsKit.Dossiers.Problem(kind, title, url, detail) is { } problem)
            throw new ArgumentException(problem, nameof(title));
        var item = new DossierItem(NewId(), eventId, kind, title.Trim(), url?.Trim() is { Length: > 0 } savedUrl ? savedUrl : null, detail.Trim(), addedBy, now);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM events WHERE id = $event)";
            exists.Parameters.AddWithValue("$event", eventId);
            if ((long)exists.ExecuteScalar()! == 0)
                throw new InvalidOperationException("That story is no longer available.");
        }
        if (item.Url is { } urlValue)
        {
            using var duplicate = connection.CreateCommand();
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT id, event_id, kind, title, url, detail, added_by, at FROM dossier_items WHERE event_id = $event AND url = $url ORDER BY at, id LIMIT 1";
            duplicate.Parameters.AddWithValue("$event", eventId);
            duplicate.Parameters.AddWithValue("$url", urlValue);
            DossierItem? existing = null;
            using (var reader = duplicate.ExecuteReader())
            {
                if (reader.Read())
                    existing = new DossierItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), At(reader, 7));
            }
            if (existing is not null)
            {
                transaction.Commit();
                return existing;
            }
        }
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM dossier_items WHERE event_id = $event";
            count.Parameters.AddWithValue("$event", eventId);
            if ((long)count.ExecuteScalar()! >= Dossiers.MaxItems)
            {
                transaction.Commit();
                return null;
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO dossier_items (id, event_id, kind, title, url, detail, added_by, at) VALUES ($id, $event, $kind, $title, $url, $detail, $by, $at)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$url", (object?)item.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", item.Detail);
        command.Parameters.AddWithValue("$by", addedBy);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
        transaction.Commit();
        return item;
    }

    public bool UnpinFromDossier(string eventId, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dossier_items WHERE id = $id AND event_id = $event";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$event", eventId);
        return command.ExecuteNonQuery() == 1;
    }

    public void UnpinFromDossier(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dossier_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // ---- N15 budget history and metrics ----

    public void RecordBudgetStatus(string eventId, string status, DateTimeOffset at, string by)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO budget_history (event_id, status, at, by) SELECT $event, $status, $at, $by "
            + "WHERE COALESCE((SELECT status FROM budget_history WHERE event_id = $event ORDER BY at DESC, rowid DESC LIMIT 1), '') <> $status";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", Unix(at));
        command.Parameters.AddWithValue("$by", by);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<(string EventId, string Status, DateTimeOffset At)> BudgetHistory()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, status, at FROM budget_history ORDER BY at";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, DateTimeOffset)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), At(reader, 2)));
        return rows;
    }

    public DeskMetrics DeskMetrics(DateTimeOffset now)
    {
        var budget = Budget();
        var history = BudgetHistory();
        var toVerdict = history.GroupBy(h => h.EventId)
            .Select(g => (First: g.Min(h => h.At), Ready: g.Where(h => h.Status is "ready" or "published").Select(h => (DateTimeOffset?)h.At).Min()))
            .Where(x => x.Ready is not null).Select(x => x.Ready!.Value - x.First);
        var verdicts = SignOffs().Where(s => s.Kind == global::GwsBusinessSuite.WorldNewsKit.SignOffs.Verdict)
            .GroupBy(s => s.EventId).Where(g => g.Select(s => s.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= global::GwsBusinessSuite.WorldNewsKit.SignOffs.Needed)
            .Select(g => (EventId: g.Key, Level: g.OrderBy(s => s.Id).Last().LabelLevel)).ToList();
        var held = 0;
        foreach (var (eventId, level) in verdicts)
        {
            var now_ = LabelSnapshots(eventId).LastOrDefault()?.Level ?? level;
            if (DeskMetricsRules.Held(level, now_)) held++;
        }
        var tips = Tips();
        return new DeskMetrics(
            budget.Count,
            budget.Where(b => b.Status != "published").GroupBy(b => b.Owner, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList(),
            DeskMetricsRules.Median(toVerdict),
            verdicts.Count, held,
            tips.Count, tips.Count(t => t.EventId is { Length: > 0 }),
            LiveUpdates(limit: 100000).Count,
            Corrections(limit: 100000).Count);
    }

    // ---- N13 the morgue: one search over the desk's records ----

    public IReadOnlyList<MorgueHit> SearchDesk(string text, string username, int limit = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (text.Length > MorgueSearch.MaxQueryLength)
            throw new ArgumentException($"Keep the search to {MorgueSearch.MaxQueryLength} characters or fewer.", nameof(text));
        limit = Math.Min(limit, MorgueSearch.MaxResults);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).Where(w => w.Length >= 2).Take(6).ToList();
        if (words.Count == 0) return [];
        using var connection = Open();
        var hits = new List<MorgueHit>();
        void Search(string sql, string? owner = null)
        {
            using var command = connection.CreateCommand();
            var where = string.Join(" AND ", words.Select((_, i) => $"instr(lower(haystack), $w{i}) > 0"));
            command.CommandText = $"SELECT kind, event_id, title, text, username, at FROM ({sql}) WHERE {where} ORDER BY at DESC LIMIT $limit";
            for (var i = 0; i < words.Count; i++) command.Parameters.AddWithValue("$w" + i, words[i]);
            command.Parameters.AddWithValue("$limit", limit);
            if (owner is not null) command.Parameters.AddWithValue("$owner", owner);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                hits.Add(new MorgueHit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), At(reader, 5)));
        }
        Search("SELECT 'note' AS kind, event_id, '' AS title, text, username, created_at AS at, text AS haystack FROM desk_notes");
        Search("SELECT 'budget' AS kind, event_id, title, status || ': ' || next_action AS text, owner AS username, updated_at AS at, title || ' ' || next_action AS haystack FROM budget");
        Search("SELECT 'tip' AS kind, COALESCE(event_id, '') AS event_id, 'Reader tip' AS title, message AS text, handled_by AS username, created_at AS at, message || ' ' || COALESCE(name, '') || ' ' || COALESCE(url, '') AS haystack FROM tips");
        Search("SELECT 'live' AS kind, event_id, status AS title, COALESCE(corrected_text, text) AS text, username, COALESCE(corrected_at, created_at) AS at, text || ' ' || COALESCE(corrected_text, '') AS haystack FROM live_updates");
        Search("SELECT 'correction' AS kind, event_id, title, text, username, at, title || ' ' || text AS haystack FROM corrections");
        Search("SELECT 'dossier' AS kind, event_id, title, detail AS text, added_by AS username, at, title || ' ' || detail AS haystack FROM dossier_items");
        Search($"SELECT 'verdict' AS kind, s.event_id, COALESCE({EnglishEventHeadline}, e.title, 'Story') AS title, s.level || ': ' || s.reason AS text, NULL AS username, s.taken_at AS at, COALESCE({EnglishEventHeadline}, e.title, '') || ' ' || s.level || ' ' || s.reason AS haystack FROM label_snapshots s LEFT JOIN events e ON e.id = s.event_id");
        Search($"SELECT 'approval' AS kind, s.event_id, COALESCE({EnglishEventHeadline}, e.title, 'Story') AS title, s.kind || ': ' || s.label_level AS text, s.username, s.at, COALESCE({EnglishEventHeadline}, e.title, '') || ' ' || s.kind || ' ' || s.label_level || ' ' || s.username AS haystack FROM sign_offs s LEFT JOIN events e ON e.id = s.event_id");
        Search("SELECT 'contact' AS kind, '' AS event_id, c.name AS title, c.organisation || ' · ' || c.notes AS text, NULL AS username, c.created_at AS at, c.name || ' ' || c.organisation || ' ' || c.notes || ' ' || COALESCE(u.note, '') AS haystack FROM source_contacts c LEFT JOIN source_uses u ON u.source_id = c.id WHERE c.owner = $owner", NormalizeUsername(username));
        Search("SELECT 'source-use' AS kind, u.event_id, c.name AS title, u.outcome || ': ' || u.note AS text, NULL AS username, u.at, c.name || ' ' || c.organisation || ' ' || u.outcome || ' ' || u.note AS haystack FROM source_uses u JOIN source_contacts c ON c.id = u.source_id WHERE c.owner = $owner", NormalizeUsername(username));
        SearchReports(connection, text, limit, hits);
        return hits.OrderByDescending(h => h.At).ThenBy(h => h.Kind, StringComparer.Ordinal).Take(limit).ToList();
    }

    private void SearchReports(SqliteConnection connection, string text, int limit, List<MorgueHit> hits)
    {
        var fullText = FullTextQuery.From(text);
        if (fullText is null) return;
        using var command = connection.CreateCommand();
        string match;
        if (FullTextReady() && fullText is not null)
        {
            match = $"((lower(a.language) = 'english' AND a.rowid IN (SELECT rowid FROM articles_fts WHERE articles_fts MATCH $query)) OR {EnglishTextMatch("a", "$query", true)})";
            command.Parameters.AddWithValue("$query", fullText);
        }
        else
        {
            match = $"((lower(a.language) = 'english' AND instr(lower(a.title || ' ' || COALESCE(a.snippet, '')), lower($query)) > 0) OR {EnglishTextMatch("a", "$query", false)})";
            command.Parameters.AddWithValue("$query", text.Trim());
        }
        command.CommandText = $"""
            SELECT a.event_id,
                   CASE
                       WHEN lower(a.language) = 'english' THEN COALESCE(NULLIF(t.english, ''), a.title)
                       WHEN t.english IS NOT NULL AND t.english <> '' AND (t.source_title IS NULL OR ({CurrentEnglish})) THEN t.english
                       ELSE 'English translation pending'
                   END,
                   CASE
                       WHEN lower(a.language) = 'english' THEN COALESCE(NULLIF(t.description, ''), a.snippet, '')
                       WHEN t.english IS NOT NULL AND t.english <> '' AND (t.source_title IS NULL OR ({CurrentEnglish})) THEN COALESCE(t.description, '')
                       ELSE ''
                   END,
                   a.seen_at
            FROM articles a
            LEFT JOIN article_translations t ON t.article_id = a.id
            WHERE a.event_id IS NOT NULL AND {match}
            ORDER BY a.seen_at DESC, a.id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            hits.Add(new MorgueHit("report", reader.GetString(0), reader.GetString(1), reader.GetString(2), null, At(reader, 3)));
    }
}
