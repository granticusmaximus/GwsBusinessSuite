using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// The offline field kit (19-11-26): a watched area as one self-contained page for the phone app,
// readable with no signal. Camera snapshots are built into the page (no network needed to show
// them), with the area's weather alerts, hazards and news. The page saves itself in the browser's
// cache (field-kit.js) and the service worker serves it offline; notes written offline are queued
// and become map notes when there's signal again (AddNoteAsync). No live map: map tiles aren't kept.
public sealed class FieldKitService(
    OverwatchAreaService areas,
    CameraDirectoryService cameras,
    CameraSnapshotDownloader snapshots,
    INwsAlertsService weather,
    HazardLayerService hazards,
    WorldNewsMapService news,
    MapNoteService notes,
    TimeProvider time,
    ILogger<FieldKitService> logger)
{
    public const int MaxCameras = 12;

    // Null when the area isn't this person's.
    public async Task<string?> PageAsync(string username, Guid areaId, CancellationToken ct = default)
    {
        var area = await areas.GetAreaAsync(username, areaId, ct);
        if (area is null) return null;
        var bounds = area.Bounds;
        var cameraList = (await TryAsync("cameras", () => cameras.GetCamerasInBoundingBoxAsync(bounds, ct)) ?? [])
            .Where(c => c.StreamKind == CameraStreamKind.Snapshot).Take(MaxCameras).ToList();
        var frames = await Task.WhenAll(cameraList.Select(async c => (Camera: c, Image: await SnapshotAsync(c, ct))));
        var alertList = await TryAsync("weather alerts", () => weather.GetActiveAlertsAsync(bounds, ct)) ?? [];
        var hazardList = (await TryAsync("hazards", () => hazards.GetHazardsAsync(bounds, ct)))?.Features ?? [];
        var now = time.GetUtcNow();
        var stories = await TryAsync("news", () => Task.Run(() => news.SignalsAt(bounds, now.AddHours(-24), null, SituationKinds.Operational), ct)) ?? [];
        return Render(area, frames, alertList, hazardList, stories, now);
    }

    public static string Render(OverwatchAreaView area, IReadOnlyList<(CameraFeed Camera, byte[]? Image)> frames, IReadOnlyList<WeatherAlert> alerts,
        IReadOnlyList<HazardFeature> hazardList, IReadOnlyList<WorldNewsMapSignal> stories, DateTimeOffset savedAt)
    {
        static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">");
        html.Append($"<title>Field kit · {H(area.Name)}</title><style>{Css}</style></head>");
        html.Append($"<body data-area=\"{area.Id}\" data-lat=\"{(area.Bounds.North + area.Bounds.South) / 2:0.#####}\" data-lon=\"{(area.Bounds.East + area.Bounds.West) / 2:0.#####}\">");
        html.Append($"<header><span class=\"eyebrow\">OVERWATCH // FIELD KIT</span><h1>{H(area.Name)}</h1>");
        html.Append($"<p>Saved {savedAt.ToLocalGwsDateTime()} · works without signal. <span id=\"kit-status\" role=\"status\"></span></p></header>");

        html.Append("<section><h2>Alerts and hazards</h2>");
        if (alerts.Count == 0 && hazardList.Count == 0) html.Append("<p class=\"quiet\">None when saved.</p>");
        html.Append("<ul>");
        foreach (var alert in alerts) html.Append($"<li><b class=\"amber\">{H(alert.Event)}</b> {H(alert.AreaDescription)}{(string.IsNullOrWhiteSpace(alert.Expires) ? "" : $" · until {H(alert.Expires)}")}</li>");
        foreach (var hazard in hazardList.Take(20))
            html.Append($"<li><b class=\"{(hazard.Severity == "major" ? "red" : hazard.Severity == "moderate" ? "amber" : "green")}\">{H(hazard.Kind.ToUpperInvariant())}</b> {H(hazard.Title)}</li>");
        html.Append("</ul></section>");

        html.Append("<section><h2>Cameras</h2>");
        if (frames.Count == 0) html.Append("<p class=\"quiet\">No still cameras in this area.</p>");
        html.Append("<div class=\"cams\">");
        foreach (var (camera, image) in frames)
        {
            html.Append("<figure>");
            if (image is { Length: > 0 }) html.Append($"<img alt=\"{H(camera.Name)}\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(image)}\">");
            else html.Append("<div class=\"noimg\">No picture when saved</div>");
            html.Append($"<figcaption>{H(camera.Name)} · {H(camera.SourceName)}</figcaption></figure>");
        }
        html.Append("</div></section>");

        html.Append("<section><h2>News in the last day</h2>");
        if (stories.Count == 0) html.Append("<p class=\"quiet\">No stories placed in this area.</p>");
        html.Append("<ul>");
        foreach (var story in stories.Take(15)) html.Append($"<li>{H(story.Title)} <span class=\"quiet\">· {story.OutletCount} outlets</span></li>");
        html.Append("</ul></section>");

        html.Append("<section><h2>Notes</h2><p class=\"quiet\">Written here, they're kept on this phone and added to your Overwatch map notes when there's signal.</p>");
        html.Append("<form id=\"kit-note\"><label for=\"kit-title\">Title</label><input id=\"kit-title\" required maxlength=\"120\">");
        html.Append("<label for=\"kit-body\">Note</label><textarea id=\"kit-body\" rows=\"3\" maxlength=\"2000\"></textarea>");
        html.Append("<button type=\"submit\">Save note</button></form><ul id=\"kit-queue\"></ul></section>");
        html.Append("<script src=\"/js/field-kit.js\"></script></body></html>");
        return html.ToString();
    }

    // A note from the kit, as a map pin at the area's middle unless the phone gave its own place.
    public Task<MapNoteView> AddNoteAsync(string username, string title, string? body, double latitude, double longitude, CancellationToken ct = default) =>
        notes.SaveAsync(username, null, MapNoteKinds.Pin, title, body, null, [new MapNotePoint(latitude, longitude)], ct);

    private const string Css = """
        :root{color-scheme:dark;--bg:#020604;--panel:#06130c;--rule:#12432a;--green:#7dffb0;--text:#c9f5dc;--muted:#6fa889;--amber:#ffb000;--red:#ff3b3b}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 ui-monospace,Menlo,monospace;padding:max(16px,env(safe-area-inset-top)) max(16px,env(safe-area-inset-right)) 32px max(16px,env(safe-area-inset-left))}
        header,section{max-width:760px;margin:0 auto 20px}h1{font-size:1.5rem;margin:.2rem 0;color:#e8fff1}h2{font-size:1rem;color:var(--green);letter-spacing:.08em;text-transform:uppercase;border-bottom:1px solid var(--rule);padding-bottom:4px}
        .eyebrow{color:var(--green);font-size:.75rem;letter-spacing:.16em}.quiet{color:var(--muted)}.green{color:var(--green)}.amber{color:var(--amber)}.red{color:var(--red)}
        ul{padding-left:1.1rem}li{margin:.3rem 0}.cams{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px}
        figure{margin:0;background:var(--panel);border:1px solid var(--rule)}figure img{width:100%;display:block}.noimg{padding:30px 10px;text-align:center;color:var(--muted)}figcaption{padding:6px 8px;font-size:.8rem;color:var(--muted)}
        form{display:grid;gap:6px}input,textarea{background:var(--panel);color:var(--text);border:1px solid var(--rule);padding:8px;font:inherit}button{background:var(--green);color:#03140b;border:0;padding:10px;font:inherit;font-weight:700}
        button:focus-visible,input:focus-visible,textarea:focus-visible{outline:2px solid var(--green);outline-offset:2px}
        """;

    private async Task<byte[]?> SnapshotAsync(CameraFeed camera, CancellationToken ct)
    {
        try { return await snapshots.DownloadAsync(camera.StreamUrl, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Field kit: no snapshot for {Camera}", camera.Id);
            return null;
        }
    }

    private async Task<T?> TryAsync<T>(string what, Func<Task<T>> load) where T : class
    {
        try { return await load(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Field kit: {What} couldn't be read", what);
            return null;
        }
    }
}
