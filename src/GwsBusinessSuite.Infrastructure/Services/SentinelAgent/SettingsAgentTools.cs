using System.Globalization;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Site settings for the agent (Phase D, 30-10-26), through ISiteSettingsService like the Settings
// page (AdminOnly). A setting changes the whole app, so changing one waits for a Confirm; Undo puts
// it back. Only the plain values below: the navigation and the default category stay on the page.
public sealed class SettingsAgentTools(ISiteSettingsService settings) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];

    private sealed record Setting(string Name, string About, Func<SiteSettingsView, string?> Get, Func<SiteSettingsView, string?, SiteSettingsView> Set);

    private static readonly Setting[] Settings =
    [
        new("postsPerPage", "posts per blog page: 10, 12, 25 or 50",
            view => view.PostsPerPage.ToString(CultureInfo.InvariantCulture),
            (view, value) => view with { PostsPerPage = Number(value) is var n and (10 or 12 or 25 or 50) ? n : throw new SentinelAgentToolException("postsPerPage is 10, 12, 25 or 50.") }),
        new("defaultAuthorByline", "the byline new posts get",
            view => view.DefaultAuthorByline,
            (view, value) => view with { DefaultAuthorByline = value }),
        new("maxMediaUploadSizeMb", "the largest media upload, in MB (up to 100)",
            view => view.MaxMediaUploadSizeMb.ToString(CultureInfo.InvariantCulture),
            (view, value) => view with { MaxMediaUploadSizeMb = Math.Clamp(Number(value), 1, 100) }),
        new("ollamaModel", "the server's AI model, instead of the default (none for the default)",
            view => view.OllamaModelOverride,
            (view, value) => view with { OllamaModelOverride = value }),
        new("ollamaTimeoutMinutes", "how long article generation may take, in minutes (up to 180; none for the default)",
            view => view.OllamaTimeoutMinutesOverride?.ToString(CultureInfo.InvariantCulture),
            (view, value) => view with { OllamaTimeoutMinutesOverride = value is null ? null : Math.Clamp(Number(value), 1, 180) }),
        new("heroImageModel", "the model that makes hero images (none for the default)",
            view => view.HeroImageModelOverride,
            (view, value) => view with { HeroImageModelOverride = value }),
        new("visionModel", "the model that reads images (none for the default)",
            view => view.VisionModelOverride,
            (view, value) => view with { VisionModelOverride = value })
    ];

    public SentinelAgentArea Area { get; } = new("settings", "site settings", ["setting", "posts per page", "byline", "upload size", "model"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "read_settings",
            "Read the site settings SentinelGPT can change, with what each one does.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading the settings",
            ReadAsync),
        new SentinelAgentTool(
            "change_setting",
            $"Change one site setting ({string.Join(", ", Settings.Select(setting => setting.Name))}). It affects the whole app, so this only proposes: the person confirms first.",
            SentinelAgentJson.Schema(
                ("name", "string", "The setting's name from read_settings", true),
                ("value", "string", "The new value, or none to clear it", true)),
            SentinelAgentRisk.Confirm, Roles,
            args => $"Proposing to change {args.OptionalText("name")}",
            ChangeAsync,
            UndoChangeAsync,
            async (args, _, ct) =>
            {
                var setting = Find(args.Text("name"));
                var current = await settings.GetSettingsAsync(ct);
                var value = Value(args);
                setting.Set(current, value);
                return $"Change the setting {setting.Name} from {Show(setting.Get(current))} to {Show(value)}";
            })
    ];

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var current = await settings.GetSettingsAsync(ct);
        return SentinelAgentToolResult.Of(new
        {
            settings = Settings.Select(setting => new { name = setting.Name, value = setting.Get(current) ?? "(default)", about = setting.About })
        });
    }

    private async Task<SentinelAgentToolResult> ChangeAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var setting = Find(args.Text("name"));
        var current = await settings.GetSettingsAsync(ct);
        var before = setting.Get(current);
        await settings.SaveSettingsAsync(setting.Set(current, Value(args)), ct);
        var after = setting.Get(await settings.GetSettingsAsync(ct));
        return SentinelAgentToolResult.Of(new { changed = setting.Name, from = before, to = after },
            new SentinelAgentChange($"Changed the setting {setting.Name} from {Show(before)} to {Show(after)}", "setting", setting.Name, setting.Name, "/admin/settings",
                SentinelAgentJson.Serialize(new SettingUndo(setting.Name, before, after))));
    }

    private async Task UndoChangeAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<SettingUndo>(undoJson);
        var setting = Find(undo.Name);
        var current = await settings.GetSettingsAsync(ct);
        if (setting.Get(current) != undo.After)
            throw new SentinelAgentToolException($"{setting.Name} has been changed again since, so it's left as it is.");
        await settings.SaveSettingsAsync(setting.Set(current, undo.Before), ct);
    }

    private static Setting Find(string name) =>
        Settings.FirstOrDefault(setting => string.Equals(setting.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new SentinelAgentToolException($"'{name}' isn't a setting SentinelGPT can change. They are: {string.Join(", ", Settings.Select(setting => setting.Name))}.");

    private static string? Value(JsonObject args) =>
        args.Text("value") is var value && value.ToLowerInvariant() is "none" or "default" or "clear" or "null" ? null : value;

    private static int Number(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new SentinelAgentToolException($"'{value}' should be a whole number.");

    private static string Show(string? value) => value is null ? "the default" : $"'{value}'";

    private sealed record SettingUndo(string Name, string? Before, string? After);
}
