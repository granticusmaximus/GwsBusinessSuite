using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.App;

public sealed record WorldEventRow(EventOverview Event, string Title, string Meta);

// World news (Mac): every event the local pipeline has grouped, busiest first, and the reports
// behind the one picked, in their own languages, each opening in the browser.
public partial class WorldNewsPage : ContentPage
{
    private static readonly (string Label, TimeSpan Range)[] Ranges =
        [("Last 24 hours", TimeSpan.FromHours(24)), ("Last 3 days", TimeSpan.FromDays(3)), ("Last 7 days", TimeSpan.FromDays(7))];

    private readonly WorldNewsService _news;
    private IReadOnlyList<WorldEventRow> _rows = [];

    public WorldNewsPage(WorldNewsService news)
    {
        InitializeComponent();
        _news = news;
        RangePicker.ItemsSource = Ranges.Select(r => r.Label).ToList();
        RangePicker.SelectedIndex = 0;
        DetailView.Source = new HtmlWebViewSource { Html = OfflineStore.Document("World news", "<p>Pick an event on the left to see every report on it.</p>") };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if MACCATALYST
        MacToolbarActions.SyncSelectedTab?.Invoke("WorldNewsPage");
#endif
        _news.Changed += OnNewsChanged;
        _news.StartAutoRefresh();
        await ReloadAsync();
    }

    protected override void OnDisappearing()
    {
        _news.Changed -= OnNewsChanged;
        base.OnDisappearing();
    }

    private void OnNewsChanged() => MainThread.BeginInvokeOnMainThread(async () => await ReloadAsync());

    private async Task ReloadAsync()
    {
        var refreshed = _news.LastRefreshed is { } at ? $"Read {at:dd-MM-yy HH:mm}. " : string.Empty;
        StatusLabel.Text = refreshed + _news.Status;
        RefreshButton.IsEnabled = !_news.IsRefreshing;
        var since = DateTimeOffset.UtcNow - Ranges[Math.Max(0, RangePicker.SelectedIndex)].Range;
        var events = await Task.Run(() => _news.Store.TopEvents(since, 300));
        _rows = events.Select(e => new WorldEventRow(e, e.Title, WorldNewsHtml.Meta(e, TimeZoneInfo.Local))).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = FilterEntry.Text?.Trim();
        EventsList.ItemsSource = string.IsNullOrEmpty(filter)
            ? _rows
            : _rows.Where(r => r.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnRangeChanged(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await _news.RefreshAsync();

    private async void OnEventSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not WorldEventRow row) return;
        var articles = await Task.Run(() => _news.Store.ArticlesForEvent(row.Event.Id));
        DetailView.Source = new HtmlWebViewSource
        {
            Html = OfflineStore.Document(row.Title, WorldNewsHtml.EventDetail(row.Event, articles, TimeZoneInfo.Local))
        };
    }

    // Report links open in the browser rather than inside the app's detail view.
    private void OnDetailNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        e.Cancel = true;
        _ = Launcher.Default.OpenAsync(uri);
    }
}
