using System.Diagnostics;
using System.Reflection;

namespace GwsBusinessSuite.App;

// "Check for Updates..." (GWS menu): the installed app rebuilds and reinstalls itself from the repo
// it was built from (scripts/install-gws-mac.sh), then reopens - no terminal needed. The repo path
// is baked in at build time (GwsSourceRoot in the .csproj). Local ad-hoc builds run outside the App
// Sandbox, so the app can start the script; a missing repo just says so.
public static class AppUpdater
{
    private static string? SourceRoot =>
        typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GwsSourceRoot")?.Value;

    private static string? Script =>
        SourceRoot is { } root && File.Exists(Path.Combine(root, "scripts", "install-gws-mac.sh"))
            ? Path.Combine(root, "scripts", "install-gws-mac.sh")
            : null;

    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "GWS Business Suite", "update.log");

    // null = couldn't tell (no repo / script failed); true = newer source waiting.
    public static async Task<(bool? Available, string? Problem)> CheckAsync()
    {
        if (Script is not { } script) return (null, $"The source folder this app was built from ({SourceRoot ?? "unknown"}) isn't on this Mac.");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/bash", [script, "--check"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null) return (null, "The update check couldn't start.");
            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                10 => (true, null),
                0 => (false, null),
                _ => (null, (await process.StandardError.ReadToEndAsync()).Trim() is { Length: > 0 } error ? error : "The update check failed.")
            };
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // Starts the rebuild + reinstall; the script quits this app and reopens the new one. Output
    // goes to ~/Library/Logs/GWS Business Suite/update.log.
    public static string? StartUpdate()
    {
        if (Script is not { } script) return "The source folder this app was built from isn't on this Mac.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // nohup + a login shell: the script outlives this app (it quits it) and finds dotnet
            // on the PATH a GUI app doesn't get.
            var command = $"nohup '{script.Replace("'", "'\\''")}' --relaunch > '{LogPath.Replace("'", "'\\''")}' 2>&1 &";
            Process.Start(new ProcessStartInfo("/bin/zsh", ["-l", "-c", command]) { UseShellExecute = false });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
