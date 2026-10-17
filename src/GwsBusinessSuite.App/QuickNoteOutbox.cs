using System.Text.Json;

namespace GwsBusinessSuite.App;

// Quick Notes made while the server can't be reached (Sentinel item 12). Instead of failing, a
// note waits here (outbox.json in the app's data folder) and is sent, oldest first, when the
// connection comes back - OfflineSyncService flushes it on launch, on reconnect and every few
// minutes. A note the server refuses for another reason stays with that reason, so nothing typed
// is ever silently dropped; it can be removed from the Offline tab.
public sealed record QueuedQuickNote(Guid Id, string Title, string Markdown, bool ToDailyNote, Guid? ContactId, Guid? BookingId, long QueuedAtUnix, string? LastError)
{
    public string Display => $"{Title} - {(LastError is null ? "waiting since " + DateTimeOffset.FromUnixTimeSeconds(QueuedAtUnix).ToLocalTime().ToString("dd-MM-yy HH:mm") : LastError)}";
}

public sealed record QuickNoteOutcome(string? Url, string? Error, bool Queued);

public sealed class QuickNoteOutbox(string path, NativeSessionClient session)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action? Changed;

    // Sends now when it can; queues on "couldn't reach the server" or an expired/missing session.
    public async Task<QuickNoteOutcome> SaveOrQueueAsync(string title, string markdown, bool toDailyNote, Guid? contactId = null, Guid? bookingId = null)
    {
        var result = await session.SaveQuickNoteAsync(title, markdown, toDailyNote, contactId, bookingId);
        if (result.Error is null) return new QuickNoteOutcome(result.Url, null, false);
        if (!NativeSessionClient.IsRetryable(result.Error)) return new QuickNoteOutcome(null, result.Error, false);
        await UpdateAsync(items => items.Add(new QueuedQuickNote(Guid.NewGuid(), title, markdown, toDailyNote, contactId, bookingId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null)));
        return new QuickNoteOutcome(null, null, true);
    }

    public async Task<IReadOnlyList<QueuedQuickNote>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return Read(); }
        finally { _lock.Release(); }
    }

    public Task RemoveAsync(Guid id) => UpdateAsync(items => items.RemoveAll(item => item.Id == id));

    // Sends what's waiting, oldest first; stops at the first connection problem. Returns how many were sent.
    public async Task<int> FlushAsync()
    {
        var sent = 0;
        foreach (var note in await ListAsync())
        {
            var result = await session.SaveQuickNoteAsync(note.Title, note.Markdown, note.ToDailyNote, note.ContactId, note.BookingId);
            if (result.Error is null)
            {
                await RemoveAsync(note.Id);
                sent++;
                continue;
            }
            if (NativeSessionClient.IsRetryable(result.Error)) break;
            await UpdateAsync(items =>
            {
                var index = items.FindIndex(item => item.Id == note.Id);
                if (index >= 0) items[index] = items[index] with { LastError = result.Error };
            });
        }
        return sent;
    }

    private async Task UpdateAsync(Action<List<QueuedQuickNote>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var items = Read();
            change(items);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(items, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke();
    }

    private List<QueuedQuickNote> Read()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<QueuedQuickNote>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (JsonException)
        {
            // Never overwrite notes we can't read: set the damaged file aside and start a new one.
            File.Move(path, $"{path}.unreadable-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", overwrite: true);
            return [];
        }
    }
}
