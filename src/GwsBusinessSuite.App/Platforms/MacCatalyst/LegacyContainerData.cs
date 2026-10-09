namespace GwsBusinessSuite.App;

// The app used to run inside App Sandbox, so its saved data (SentinelGPT sessions, approved
// memory, grounding key, device secret) lives in ~/Library/Containers/<bundle id>/Data/Library.
// Today's local build is only linker-signed (the entitlements aren't applied), so it runs
// unsandboxed and FileSystem.AppDataDirectory is plain ~/Library - none of that data was visible
// any more. This copies it across once, never overwriting anything already saved in the new place.
internal static class LegacyContainerData
{
    private const string Prefix = "sentinelgpt-";

    public static void CopyMissingIntoAppData()
    {
        try
        {
            var dataDirectory = FileSystem.Current.AppDataDirectory;
            if (dataDirectory.Contains("/Containers/", StringComparison.Ordinal)) return; // still sandboxed

            var container = Path.Combine(dataDirectory, "Containers", AppInfo.Current.PackageName, "Data", "Library");
            if (!Directory.Exists(container)) return;

            foreach (var file in Directory.EnumerateFiles(container, Prefix + "*"))
            {
                var target = Path.Combine(dataDirectory, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }

            foreach (var directory in Directory.EnumerateDirectories(container, Prefix + "*"))
            {
                CopyDirectoryWithoutOverwriting(directory, Path.Combine(dataDirectory, Path.GetFileName(directory)));
            }
        }
        catch (Exception ex)
        {
            // Starting with empty history is better than not starting at all.
            System.Diagnostics.Debug.WriteLine($"Copying data from the old app container failed: {ex}");
        }
    }

    private static void CopyDirectoryWithoutOverwriting(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Copy(file, destination);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryWithoutOverwriting(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
