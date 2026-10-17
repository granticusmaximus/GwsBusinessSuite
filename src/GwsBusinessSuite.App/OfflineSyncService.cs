namespace GwsBusinessSuite.App;

// Offline reading (Sentinel item 12): sends waiting Quick Notes and refreshes the offline page
// copies - shortly after launch, whenever the network comes back, and every five minutes.
public sealed class OfflineSyncService(NativeSessionClient session, OfflineStore store, QuickNoteOutbox outbox)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _running = new(1, 1);
    private bool _started;

    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = string.Empty;

    public void Start()
    {
        if (_started) return;
        _started = true;
        Connectivity.Current.ConnectivityChanged += (_, args) =>
        {
            if (args.NetworkAccess == NetworkAccess.Internet) _ = SyncAsync();
        };
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        // Give the Workspace tab a moment to sign in (the session comes from its cookies).
        await Task.Delay(TimeSpan.FromSeconds(20));
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await SyncAsync();
        }
        while (await timer.WaitForNextTickAsync());
    }

    // Returns a short summary for the Offline tab.
    public async Task<string> SyncAsync()
    {
        if (!await _running.WaitAsync(0)) return Status;
        try
        {
            var sent = await outbox.FlushAsync();
            var (pages, error) = await session.GetOfflinePagesAsync();
            if (pages is not null)
            {
                await store.ReplaceAllAsync(pages, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                SetStatus($"Synced {pages.Count} {(pages.Count == 1 ? "page" : "pages")}{(sent > 0 ? $", sent {sent} waiting {(sent == 1 ? "note" : "notes")}" : string.Empty)}.");
            }
            else
            {
                SetStatus($"Showing the copies from the last sync - {error}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            SetStatus($"Sync failed: {ex.Message}");
        }
        finally
        {
            _running.Release();
        }
        return Status;
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
