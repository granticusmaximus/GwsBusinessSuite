using System.Text.Json;

namespace GwsBusinessSuite.App;

// Offline reading (Sentinel item 12): read-only copies of the Sentinel pages marked "Keep offline",
// as the server rendered them, kept in the app's data folder so the Offline tab can show them with
// no connection. Each sync replaces the whole set (a page no longer kept, or no longer shared, is
// removed). Files: offline/index.json + offline/<page id>.html.
public sealed record OfflinePageEntry(Guid Id, string Title, string? Icon, long UpdatedAtUnix)
{
    public string Display => $"{(string.IsNullOrWhiteSpace(Icon) ? "📄" : Icon)} {Title}";
    public string Updated => "Updated " + DateTimeOffset.FromUnixTimeSeconds(UpdatedAtUnix).ToLocalTime().ToString("dd-MM-yy HH:mm");
}

public sealed record OfflineIndex(long? LastSyncedUnix, IReadOnlyList<OfflinePageEntry> Pages);

public sealed class OfflineStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string IndexPath => Path.Combine(folder, "index.json");

    public async Task<OfflineIndex> ReadIndexAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(IndexPath)) return new OfflineIndex(null, []);
            return JsonSerializer.Deserialize<OfflineIndex>(await File.ReadAllTextAsync(IndexPath), Json) ?? new OfflineIndex(null, []);
        }
        catch (JsonException)
        {
            return new OfflineIndex(null, []);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> ReadHtmlAsync(Guid pageId)
    {
        var path = Path.Combine(folder, $"{pageId:N}.html");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<(OfflinePageEntry Entry, string Html)> pages, long syncedUnix)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(folder);
            var keep = new HashSet<string>(pages.Select(page => $"{page.Entry.Id:N}.html"), StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, html) in pages)
            {
                var path = Path.Combine(folder, $"{entry.Id:N}.html");
                var temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, html);
                File.Move(temp, path, overwrite: true);
            }
            foreach (var stale in Directory.EnumerateFiles(folder, "*.html").Where(file => !keep.Contains(Path.GetFileName(file))))
                File.Delete(stale);
            var index = new OfflineIndex(syncedUnix, pages.Select(page => page.Entry).ToList());
            var indexTemp = IndexPath + ".tmp";
            await File.WriteAllTextAsync(indexTemp, JsonSerializer.Serialize(index, Json));
            File.Move(indexTemp, IndexPath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    // The stored page HTML in a small self-contained document: the app's colours, no scripts,
    // images shown only from https (the server's own /media and /admin paths need the session,
    // which isn't there offline - they show as their alt text).
    public static string Document(string title, string bodyHtml) => $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; style-src 'unsafe-inline'">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          body { margin: 0; padding: 24px 32px; background: #141210; color: #e7e5e4; font: 15px/1.6 -apple-system, BlinkMacSystemFont, sans-serif; }
          h1.page-title { font-size: 28px; margin: 0 0 16px; }
          a { color: #fbbf24; } img { max-width: 100%; } pre, code { background: #1c1917; border-radius: 4px; }
          pre { padding: 12px; overflow: auto; } table { border-collapse: collapse; } td, th { border: 1px solid #44403c; padding: 4px 8px; }
          blockquote { border-left: 3px solid #57534e; margin-left: 0; padding-left: 12px; color: #a8a29e; }
          .wiki-canvas-static { border: 1px solid #44403c; border-radius: 8px; }
        </style></head>
        <body><h1 class="page-title">{{System.Net.WebUtility.HtmlEncode(title)}}</h1>{{bodyHtml}}</body></html>
        """;
}
