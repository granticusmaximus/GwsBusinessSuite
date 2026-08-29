#if MACCATALYST
using Foundation;
#endif

namespace GwsBusinessSuite.App;

// Persists the Developer Mode workspace folder across app relaunches via a macOS security-scoped
// bookmark - the App Sandbox only remembers a user-picked path for the lifetime of the process
// that received it from FolderPicker unless it's captured this way (see
// project_developer_mode_sentinelgpt / SentinelGptPage's own comment on this limitation). Only
// implemented for MacCatalyst: iOS/Android/Windows builds of this shared page keep today's
// pick-every-time behavior, which is not a regression there.
public sealed class WorkspaceBookmarkStore(string filePath)
{
    public async Task<string?> TryResolveAsync()
    {
        if (!File.Exists(filePath)) return null;

#if MACCATALYST
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            var bookmark = NSData.FromArray(bytes);
            var url = NSUrl.FromBookmarkData(
                bookmark, NSUrlBookmarkResolutionOptions.WithSecurityScope, null, out var isStale, out var error);

            if (url is null || error is not null)
            {
                Remove();
                return null;
            }

            if (!url.StartAccessingSecurityScopedResource())
            {
                return null;
            }

            if (isStale)
            {
                await PersistAsync(url);
            }

            return url.Path;
        }
        catch
        {
            // A corrupt or unreadable bookmark file just means falling back to "pick again" -
            // not worth surfacing as an error to the user.
            Remove();
            return null;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    public async Task PersistAsync(string folderPath)
    {
#if MACCATALYST
        await PersistAsync(NSUrl.FromFilename(folderPath));
#else
        await Task.CompletedTask;
#endif
    }

#if MACCATALYST
    private async Task PersistAsync(NSUrl url)
    {
        try
        {
            var bookmark = url.CreateBookmarkData(NSUrlBookmarkCreationOptions.WithSecurityScope, [], null, out var error);
            if (bookmark is null || error is not null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllBytesAsync(filePath, bookmark.ToArray());
        }
        catch
        {
            // Best-effort - the folder still works for this session even if persistence fails.
        }
    }
#endif

    public void Remove()
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }
}
