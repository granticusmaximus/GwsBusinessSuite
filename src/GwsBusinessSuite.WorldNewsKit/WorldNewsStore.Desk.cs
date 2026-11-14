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
        CREATE TABLE IF NOT EXISTS corrections (id TEXT PRIMARY KEY, event_id TEXT NOT NULL, title TEXT NOT NULL, text TEXT NOT NULL, username TEXT NOT NULL, at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS dossier_items (
            id TEXT PRIMARY KEY, event_id TEXT NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, url TEXT, detail TEXT NOT NULL, added_by TEXT NOT NULL, at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_dossier_items_event ON dossier_items (event_id, at);
        CREATE TABLE IF NOT EXISTS budget_history (event_id TEXT NOT NULL, status TEXT NOT NULL, at INTEGER NOT NULL, by TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_budget_history_event ON budget_history (event_id, at);
        """;

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static long Unix(DateTimeOffset at) => at.ToUnixTimeSeconds();
    private static DateTimeOffset At(SqliteDataReader reader, int i) => DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(i));

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
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM beats WHERE id = $id AND username = $user; DELETE FROM beat_hits WHERE beat_id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", NormalizeUsername(username));
        command.ExecuteNonQuery();
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
                using var hit = connection.CreateCommand();
                hit.CommandText = "INSERT OR IGNORE INTO beat_hits (beat_id, event_id) VALUES ($beat, $event)";
                hit.Parameters.AddWithValue("$beat", beat.Id);
                hit.Parameters.AddWithValue("$event", e.Id);
                hit.ExecuteNonQuery();
                var situationId = SituationOf(e.Id);
                if (situationId is null || (mayAlert is not null && !mayAlert(situationId))) continue;
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

    // ---- N7 desk notes ----

    public IReadOnlyList<DeskNote> DeskNotes(string? eventId = null, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, username, text, created_at FROM desk_notes" + (eventId is null ? "" : " WHERE event_id = $event") + " ORDER BY created_at DESC LIMIT $limit";
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
        text = text.Trim();
        if (text.Length > global::GwsBusinessSuite.WorldNewsKit.DeskNotes.MaxLength) text = text[..global::GwsBusinessSuite.WorldNewsKit.DeskNotes.MaxLength];
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

    public IReadOnlyList<SourceContact> SourceContacts(string owner)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, owner, name, organisation, contact, reliability, notes, created_at FROM source_contacts WHERE owner = $owner ORDER BY name";
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        using var reader = command.ExecuteReader();
        var contacts = new List<SourceContact>();
        while (reader.Read())
            contacts.Add(new SourceContact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6), At(reader, 7)));
        return contacts;
    }

    public SourceContact SaveSourceContact(SourceContact contact)
    {
        var saved = contact with { Owner = NormalizeUsername(contact.Owner), Id = string.IsNullOrEmpty(contact.Id) ? NewId() : contact.Id };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_contacts (id, owner, name, organisation, contact, reliability, notes, created_at) VALUES ($id, $owner, $name, $org, $contact, $rel, $notes, $at)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, organisation = excluded.organisation, contact = excluded.contact, reliability = excluded.reliability, notes = excluded.notes
            WHERE source_contacts.owner = excluded.owner
            """;
        command.Parameters.AddWithValue("$id", saved.Id);
        command.Parameters.AddWithValue("$owner", saved.Owner);
        command.Parameters.AddWithValue("$name", saved.Name.Trim());
        command.Parameters.AddWithValue("$org", saved.Organisation.Trim());
        command.Parameters.AddWithValue("$contact", saved.Contact.Trim());
        command.Parameters.AddWithValue("$rel", Math.Clamp(saved.Reliability, 1, 5));
        command.Parameters.AddWithValue("$notes", saved.Notes.Trim());
        command.Parameters.AddWithValue("$at", Unix(saved.CreatedAt));
        command.ExecuteNonQuery();
        return saved;
    }

    public void RemoveSourceContact(string owner, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM source_uses WHERE source_id = $id AND EXISTS (SELECT 1 FROM source_contacts WHERE id = $id AND owner = $owner); DELETE FROM source_contacts WHERE id = $id AND owner = $owner";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        command.ExecuteNonQuery();
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
    public IReadOnlyList<SourceUse> SourceUses(string owner, string? eventId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.source_id, u.event_id, u.outcome, u.note, u.at FROM source_uses u JOIN source_contacts c ON c.id = u.source_id WHERE c.owner = $owner"
            + (eventId is null ? "" : " AND u.event_id = $event") + " ORDER BY u.at DESC";
        command.Parameters.AddWithValue("$owner", NormalizeUsername(owner));
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        var uses = new List<SourceUse>();
        while (reader.Read()) uses.Add(new SourceUse(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), At(reader, 4)));
        return uses;
    }

    // ---- N9 sign-offs ----

    public IReadOnlyList<SignOff> SignOffs(string? eventId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
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
        using var transaction = connection.BeginTransaction();
        string previous;
        using (var last = connection.CreateCommand())
        {
            last.CommandText = "SELECT hash FROM sign_offs ORDER BY id DESC LIMIT 1";
            previous = last.ExecuteScalar() as string ?? LabelLedger.Genesis;
        }
        var hash = global::GwsBusinessSuite.WorldNewsKit.SignOffs.HashOf(previous, eventId, kind, username, labelLevel, now);
        using var insert = connection.CreateCommand();
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

    public IReadOnlyList<LiveUpdate> LiveUpdates(string? eventId = null, int limit = 200)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, username, text, status, created_at, corrected_text, corrected_at FROM live_updates"
            + (eventId is null ? "" : " WHERE event_id = $event") + " ORDER BY created_at DESC LIMIT $limit";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var updates = new List<LiveUpdate>();
        while (reader.Read())
            updates.Add(new LiveUpdate(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), At(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : At(reader, 7)));
        return updates;
    }

    public LiveUpdate AddLiveUpdate(string eventId, string username, string text, string status, DateTimeOffset now)
    {
        var update = new LiveUpdate(NewId(), eventId, username, text.Trim(), status, now, null, null);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO live_updates (id, event_id, username, text, status, created_at) VALUES ($id, $event, $user, $text, $status, $at)";
        command.Parameters.AddWithValue("$id", update.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$text", update.Text);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
        return update;
    }

    // A correction keeps the original text and shows the new one in its place.
    public void CorrectLiveUpdate(string id, string correctedText, string status, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_updates SET corrected_text = $text, corrected_at = $at, status = $status WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$text", correctedText.Trim());
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
    }

    // Stories with at least one live update, newest activity first (for the public live index).
    public IReadOnlyList<(string EventId, DateTimeOffset LastAt, int Updates)> LiveStories(int limit = 50)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, MAX(created_at), COUNT(*) FROM live_updates GROUP BY event_id ORDER BY MAX(created_at) DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<(string, DateTimeOffset, int)>();
        while (reader.Read()) list.Add((reader.GetString(0), At(reader, 1), reader.GetInt32(2)));
        return list;
    }

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

    public StoryHold? Hold(string eventId) => Holds().GetValueOrDefault(eventId);

    public void SetHold(StoryHold hold)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO holds (event_id, until, reason, set_by, set_at) VALUES ($event, $until, $reason, $by, $at) "
            + "ON CONFLICT(event_id) DO UPDATE SET until = excluded.until, reason = excluded.reason, set_by = excluded.set_by, set_at = excluded.set_at";
        command.Parameters.AddWithValue("$event", hold.EventId);
        command.Parameters.AddWithValue("$until", Unix(hold.Until));
        command.Parameters.AddWithValue("$reason", hold.Reason.Trim());
        command.Parameters.AddWithValue("$by", hold.SetBy);
        command.Parameters.AddWithValue("$at", Unix(hold.SetAt));
        command.ExecuteNonQuery();
    }

    public void ReleaseHold(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM holds WHERE event_id = $event";
        command.Parameters.AddWithValue("$event", eventId);
        command.ExecuteNonQuery();
    }

    // Whether any story in a situation is on hold now (alerts are per situation).
    public bool IsSituationHeld(string situationId, DateTimeOffset now)
    {
        var holds = Holds();
        return EventsForSituation(situationId).Any(e => global::GwsBusinessSuite.WorldNewsKit.Holds.IsHeld(holds.GetValueOrDefault(e.Id), now));
    }

    // ---- N12 corrections ----

    public IReadOnlyList<Correction> Corrections(string? eventId = null, int limit = 200)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, title, text, username, at FROM corrections" + (eventId is null ? "" : " WHERE event_id = $event") + " ORDER BY at DESC LIMIT $limit";
        if (eventId is not null) command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<Correction>();
        while (reader.Read()) list.Add(new Correction(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), At(reader, 5)));
        return list;
    }

    public Correction AddCorrection(string eventId, string title, string text, string username, DateTimeOffset now)
    {
        var correction = new Correction(NewId(), eventId, title, text.Trim(), username, now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO corrections (id, event_id, title, text, username, at) VALUES ($id, $event, $title, $text, $user, $at)";
        command.Parameters.AddWithValue("$id", correction.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$text", correction.Text);
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
        return correction;
    }

    // ---- N14 dossiers ----

    public IReadOnlyList<DossierItem> Dossier(string eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_id, kind, title, url, detail, added_by, at FROM dossier_items WHERE event_id = $event ORDER BY at";
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
        var existing = Dossier(eventId);
        if (existing.Count >= global::GwsBusinessSuite.WorldNewsKit.Dossiers.MaxItems) return null;
        if (url is not null && existing.Any(i => i.Url == url)) return existing.First(i => i.Url == url);
        var item = new DossierItem(NewId(), eventId, kind, title.Trim(), url, detail.Trim(), addedBy, now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dossier_items (id, event_id, kind, title, url, detail, added_by, at) VALUES ($id, $event, $kind, $title, $url, $detail, $by, $at)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", item.Detail);
        command.Parameters.AddWithValue("$by", addedBy);
        command.Parameters.AddWithValue("$at", Unix(now));
        command.ExecuteNonQuery();
        return item;
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
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).Where(w => w.Length >= 2).Take(6).ToList();
        if (words.Count == 0) return [];
        using var connection = Open();
        var hits = new List<MorgueHit>();
        void Search(string kind, string sql, string? owner = null)
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
            _ = kind;
        }
        Search("note", "SELECT 'note' AS kind, event_id, '' AS title, text, username, created_at AS at, text AS haystack FROM desk_notes");
        Search("budget", "SELECT 'budget' AS kind, event_id, title, status || ': ' || next_action AS text, owner AS username, updated_at AS at, title || ' ' || next_action AS haystack FROM budget");
        Search("live", "SELECT 'live' AS kind, event_id, status AS title, COALESCE(corrected_text, text) AS text, username, created_at AS at, text || ' ' || COALESCE(corrected_text, '') AS haystack FROM live_updates");
        Search("correction", "SELECT 'correction' AS kind, event_id, title, text, username, at, title || ' ' || text AS haystack FROM corrections");
        Search("dossier", "SELECT 'dossier' AS kind, event_id, title, detail AS text, added_by AS username, at, title || ' ' || detail AS haystack FROM dossier_items");
        Search("contact", "SELECT 'contact' AS kind, '' AS event_id, name AS title, organisation || ' · ' || notes AS text, NULL AS username, created_at AS at, name || ' ' || organisation || ' ' || notes AS haystack FROM source_contacts WHERE owner = $owner", NormalizeUsername(username));
        return hits.OrderByDescending(h => h.At).Take(limit).ToList();
    }
}
