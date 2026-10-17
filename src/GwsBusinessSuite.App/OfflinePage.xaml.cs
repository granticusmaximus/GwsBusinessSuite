namespace GwsBusinessSuite.App;

// Offline reading (Sentinel item 12): the pages kept offline, readable without a connection, and
// the Quick Notes waiting to be sent.
public partial class OfflinePage : ContentPage
{
    private readonly OfflineStore _store;
    private readonly QuickNoteOutbox _outbox;
    private readonly OfflineSyncService _sync;

    public OfflinePage(OfflineStore store, QuickNoteOutbox outbox, OfflineSyncService sync)
    {
        InitializeComponent();
        _store = store;
        _outbox = outbox;
        _sync = sync;
        PageView.Source = new HtmlWebViewSource { Html = OfflineStore.Document("Offline pages", "<p>Pick a page on the left.</p>") };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _sync.StatusChanged += OnSyncStatus;
        _outbox.Changed += OnOutboxChanged;
        await ReloadAsync();
    }

    protected override void OnDisappearing()
    {
        _sync.StatusChanged -= OnSyncStatus;
        _outbox.Changed -= OnOutboxChanged;
        base.OnDisappearing();
    }

    private void OnSyncStatus(string status) => MainThread.BeginInvokeOnMainThread(async () => await ReloadAsync());

    private void OnOutboxChanged() => MainThread.BeginInvokeOnMainThread(async () => await ReloadOutboxAsync());

    private async Task ReloadAsync()
    {
        var index = await _store.ReadIndexAsync();
        PagesList.ItemsSource = index.Pages;
        var synced = index.LastSyncedUnix is { } unix
            ? "Last synced " + DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd-MM-yy HH:mm") + "."
            : "Not synced yet.";
        StatusLabel.Text = string.IsNullOrEmpty(_sync.Status) ? synced : $"{synced} {_sync.Status}";
        await ReloadOutboxAsync();
    }

    private async Task ReloadOutboxAsync()
    {
        var waiting = await _outbox.ListAsync();
        OutboxList.ItemsSource = waiting;
        OutboxLabel.Text = waiting.Count == 0 ? "No notes waiting to send." : $"{waiting.Count} {(waiting.Count == 1 ? "note" : "notes")} waiting to send:";
    }

    private async void OnSyncClicked(object? sender, EventArgs e)
    {
        SyncButton.IsEnabled = false;
        StatusLabel.Text = "Syncing...";
        try
        {
            await _sync.SyncAsync();
            await ReloadAsync();
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private async void OnPageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not OfflinePageEntry entry) return;
        var html = await _store.ReadHtmlAsync(entry.Id) ?? "<p>This copy is missing - sync again.</p>";
        PageView.Source = new HtmlWebViewSource { Html = OfflineStore.Document(entry.Title, html) };
    }

    private async void OnRemoveQueuedClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: Guid id }) return;
        if (!await DisplayAlertAsync("Remove note", "Remove this note without sending it? It can't be recovered.", "Remove", "Keep")) return;
        await _outbox.RemoveAsync(id);
    }
}
