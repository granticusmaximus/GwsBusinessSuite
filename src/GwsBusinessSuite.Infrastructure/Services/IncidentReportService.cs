using System.Globalization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record IncidentReportSubject(
    string Title, string Category, string Severity, string Road, string Description, string SourceName,
    double Latitude, double Longitude, DateTimeOffset? FirstSeen = null, DateTimeOffset? LastSeen = null,
    string? Lanes = null, DateTimeOffset? EndsAt = null, string? StillListed = null);

public sealed record IncidentReportFrame(string Camera, DateTimeOffset At, byte[] Image, string Caption);

public sealed record IncidentReportData(
    IncidentReportSubject Subject, DateTimeOffset GeneratedAt, string GeneratedBy,
    byte[]? MapImage, byte[]? MapLabels, WeatherSnapshot? Weather, IReadOnlyList<WeatherAlert> Alerts,
    IReadOnlyList<IncidentReportFrame> Frames, IReadOnlyList<string> Notes);

// Overwatch incident report: one click gives a PDF of an incident - its details, an aerial map
// of the spot, the weather and NWS alerts there, and camera frames: the nearest cameras' current
// frames for a live incident, or the recorded before/during frames for an incident capture.
// Rendered with QuestPDF (Community licence, as ResumePdfService) in the container's Liberation fonts.
public sealed class IncidentReportService(
    HttpClient http,
    IAppDbContextFactory dbContextFactory,
    TrafficIncidentDirectoryService incidents,
    CameraDirectoryService cameras,
    CameraSnapshotDownloader snapshots,
    CameraTimelapseStore frames,
    IncidentCaptureService captures,
    INwsForecastService forecast,
    INwsAlertsService alerts,
    TimeProvider timeProvider,
    ILogger<IncidentReportService> logger)
{
    public const int MaxLiveCameras = 4;
    public const int MaxFramesPerCaptureCamera = 6;
    private const double NearbyCameraMiles = 1.0;
    private const string MapServer = "https://server.arcgisonline.com/ArcGIS/rest/services/";

    static IncidentReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // A live incident (still in its source's feed), or one Overwatch's history saw earlier.
    public async Task<IncidentReportData?> ForIncidentAsync(string username, string source, string id, double latitude, double longitude, CancellationToken ct = default)
    {
        var near = new BoundingBox(latitude + 0.02, latitude - 0.02, longitude + 0.02, longitude - 0.02);
        IncidentReportSubject? subject = null;
        try
        {
            var live = (await incidents.GetIncidentsInBoundingBoxAsync(near, ct)).FirstOrDefault(i => i.SourceName == source && i.Id == id);
            if (live is not null)
            {
                subject = new IncidentReportSubject($"{IncidentCategories.SingularLabel(live.Category)} on {live.RoadwayName}",
                    IncidentCategories.Label(live.Category), live.SeverityLevel, live.RoadwayName, live.Description, live.SourceName,
                    live.Latitude, live.Longitude, Lanes: live.Lanes, EndsAt: live.EndsAt, StillListed: "Listed by the source when this report was made.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Incident report: live lookup failed");
        }
        if (subject is null)
        {
            var key = $"{source}:{id}";
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var seen = await db.OverwatchIncidentSightings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
            if (seen is null) return null;
            subject = new IncidentReportSubject($"{IncidentCategories.SingularLabel(seen.Category)} on {seen.RoadwayName}",
                IncidentCategories.Label(seen.Category), seen.SeverityLevel, seen.RoadwayName, seen.Description, seen.SourceName,
                seen.Latitude, seen.Longitude, DateTimeOffset.FromUnixTimeSeconds(seen.FirstSeenUnix), DateTimeOffset.FromUnixTimeSeconds(seen.LastSeenUnix),
                StillListed: "No longer listed by the source; details are from Overwatch's incident history.");
        }

        var nearby = new List<IncidentReportFrame>();
        try
        {
            var radius = NearbyCameraMiles / 69.0;
            var box = new BoundingBox(subject.Latitude + radius, subject.Latitude - radius, subject.Longitude + radius * 1.3, subject.Longitude - radius * 1.3);
            var closest = (await cameras.GetCamerasInBoundingBoxAsync(box, ct))
                .Where(c => c.StreamKind == CameraStreamKind.Snapshot)
                .OrderBy(c => Miles(subject.Latitude, subject.Longitude, c.Latitude, c.Longitude))
                .Take(MaxLiveCameras).ToList();
            var downloads = await Task.WhenAll(closest.Select(c => snapshots.DownloadAsync(c.StreamUrl, ct)));
            var now = timeProvider.GetUtcNow();
            for (var i = 0; i < closest.Count; i++)
            {
                if (downloads[i] is not { Length: > 0 } image || !IsUsableImage(image)) continue;
                var miles = Miles(subject.Latitude, subject.Longitude, closest[i].Latitude, closest[i].Longitude);
                nearby.Add(new IncidentReportFrame(closest[i].Name, now, image, $"{closest[i].Name} · {miles:0.0} mi away · now"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Incident report: nearby cameras failed");
        }
        return await CompleteAsync(username, subject, nearby, ct);
    }

    // An incident capture: its recorded frames, a spread from before to the end of recording.
    public async Task<IncidentReportData?> ForCaptureAsync(string username, Guid captureId, CancellationToken ct = default)
    {
        var capture = captures.ListForUser(username).FirstOrDefault(c => c.Id == captureId);
        if (capture is null) return null;
        var subject = new IncidentReportSubject(capture.Title, "Incident capture", string.Empty, capture.AreaName, $"Captured in watched area \"{capture.AreaName}\".",
            "Overwatch", capture.Latitude, capture.Longitude, capture.StartedAt, capture.Until);
        var recorded = new List<IncidentReportFrame>();
        foreach (var camera in capture.Cameras)
        {
            var list = frames.ListFrames(camera.FrameKey);
            foreach (var frame in Spread(list, MaxFramesPerCaptureCamera))
            {
                if (frames.FramePath(camera.FrameKey, frame.FileName) is not { } path) continue;
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (!IsUsableImage(bytes)) continue;
                var when = frame.CapturedAt < capture.StartedAt ? "before" : "during";
                recorded.Add(new IncidentReportFrame(camera.Name, frame.CapturedAt, bytes,
                    $"{camera.Name} · {frame.CapturedAt.ToLocalGwsDateTime()} · {when}"));
            }
        }
        return await CompleteAsync(username, subject, recorded.OrderBy(f => f.At).ToList(), ct);
    }

    private async Task<IncidentReportData> CompleteAsync(string username, IncidentReportSubject subject, IReadOnlyList<IncidentReportFrame> frameList, CancellationToken ct)
    {
        var notes = new List<string>();
        var mapTask = MapAsync("World_Imagery", "jpg", subject, ct);
        var labelsTask = MapAsync("Reference/World_Transportation", "png32", subject, ct);
        var weatherTask = Optional(() => forecast.GetSnapshotAsync(subject.Latitude, subject.Longitude, ct), "weather");
        var alertsTask = Optional<IReadOnlyList<WeatherAlert>>(async () => await alerts.GetActiveAlertsAsync(
            new BoundingBox(subject.Latitude + 0.05, subject.Latitude - 0.05, subject.Longitude + 0.05, subject.Longitude - 0.05), ct), "alerts");
        await Task.WhenAll(mapTask, labelsTask, weatherTask, alertsTask);
        if (mapTask.Result is null) notes.Add("The aerial map couldn't be fetched.");
        if (weatherTask.Result is null) notes.Add("NWS weather wasn't available for this spot (it covers the U.S. only).");
        if (frameList.Count == 0) notes.Add("No still-image camera frames were available.");
        return new IncidentReportData(subject, timeProvider.GetUtcNow(), username, mapTask.Result, labelsTask.Result,
            weatherTask.Result, alertsTask.Result ?? [], frameList, notes);
    }

    // Esri's key-less static export, centred on the incident (~0.6 mi across).
    private async Task<byte[]?> MapAsync(string service, string format, IncidentReportSubject subject, CancellationToken ct)
    {
        const double HalfHeight = 0.004;
        var halfWidth = HalfHeight * 1.8 / Math.Max(0.2, Math.Cos(subject.Latitude * Math.PI / 180));
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{MapServer}{service}/MapServer/export?bbox={subject.Longitude - halfWidth},{subject.Latitude - HalfHeight},{subject.Longitude + halfWidth},{subject.Latitude + HalfHeight}&bboxSR=4326&imageSR=3857&size=900,500&format={format}&transparent=true&f=image");
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return IsUsableImage(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Incident report: map export failed");
            return null;
        }
    }

    private async Task<T?> Optional<T>(Func<Task<T?>> load, string what) where T : class
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Incident report: {What} failed", what);
            return null;
        }
    }

    public static IReadOnlyList<TimelapseFrame> Spread(IReadOnlyList<TimelapseFrame> ordered, int max)
    {
        if (ordered.Count <= max || max < 2) return ordered;
        var step = (ordered.Count - 1) / (double)(max - 1);
        return Enumerable.Range(0, max).Select(i => ordered[(int)Math.Round(i * step)]).Distinct().ToList();
    }

    // QuestPDF throws while rendering on bytes it can't decode, so anything else is left out up front.
    private static bool IsUsableImage(byte[] bytes)
    {
        try
        {
            Image.FromBinaryData(bytes);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        var dx = (lon2 - lon1) * 69.172 * Math.Cos((lat1 + lat2) / 2 * Math.PI / 180);
        var dy = (lat2 - lat1) * 69.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static string FileName(IncidentReportData data) =>
        $"incident-report-{data.GeneratedAt.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.pdf";

    // ---- PDF ----

    private const string BodyFont = "Liberation Sans";
    private const string Ink = "#1F2933";
    private const string Muted = "#6B7280";
    private const string Accent = "#0B6E4F";
    private const string Rule = "#D1D5DB";

    public static byte[] Render(IncidentReportData data)
    {
        var s = data.Subject;
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(9.5f).FontColor(Ink));

            page.Header().Column(header =>
            {
                header.Item().Text("OVERWATCH INCIDENT REPORT").FontSize(8).Bold().FontColor(Accent).LetterSpacing(0.08f);
                header.Item().PaddingTop(2).Text(s.Title).FontSize(17).Bold();
                header.Item().PaddingTop(2).Text($"Generated {data.GeneratedAt.ToLocalGwsDateTime()} by {data.GeneratedBy}").FontSize(8.5f).FontColor(Muted);
                header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Rule);
            });

            page.Content().PaddingTop(10).Column(body =>
            {
                body.Spacing(10);
                body.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.ConstantColumn(110); c.RelativeColumn(); });
                    void Row(string label, string? value)
                    {
                        if (string.IsNullOrWhiteSpace(value)) return;
                        table.Cell().PaddingVertical(2).Text(label).FontColor(Muted);
                        table.Cell().PaddingVertical(2).Text(value);
                    }
                    Row("Type", s.Category);
                    Row("Severity", string.IsNullOrEmpty(s.Severity) || s.Severity == IncidentSeverityLevels.Unknown ? null : s.Severity);
                    Row("Road", s.Road);
                    Row("Details", s.Description);
                    Row("Lanes", s.Lanes);
                    Row("Location", string.Create(CultureInfo.InvariantCulture, $"{s.Latitude:0.00000}, {s.Longitude:0.00000}"));
                    Row("First seen", s.FirstSeen?.ToLocalGwsDateTime());
                    Row("Last seen / until", s.LastSeen?.ToLocalGwsDateTime());
                    Row("Expected end", s.EndsAt?.ToLocalGwsDateTime());
                    Row("Source", s.SourceName);
                    Row("Status", s.StillListed);
                });

                if (data.MapImage is { } map)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text("Map").Bold().FontColor(Accent);
                        section.Item().PaddingTop(4).Layers(layers =>
                        {
                            layers.PrimaryLayer().Image(map).FitWidth();
                            if (data.MapLabels is { } labels) layers.Layer().Image(labels).FitWidth();
                            // The export is centred on the incident: mark the middle.
                            layers.Layer().AlignCenter().AlignMiddle().Width(14).Height(14)
                                .Border(2).BorderColor(Colors.White).Background("#E11D48");
                        });
                        section.Item().PaddingTop(2).Text("Imagery: Esri World Imagery. About 0.6 miles across; the red square is the incident.").FontSize(7.5f).FontColor(Muted);
                    });
                }

                body.Item().Column(section =>
                {
                    section.Item().Text("Weather").Bold().FontColor(Accent);
                    if (data.Weather is { } w)
                    {
                        var now = w.CurrentTemperatureFahrenheit is { } temp
                            ? $"Now {temp:0}°F{(string.IsNullOrWhiteSpace(w.CurrentConditions) ? "" : $", {w.CurrentConditions}")}. "
                            : string.Empty;
                        section.Item().PaddingTop(2).Text($"{now}Forecast: {w.ShortForecast}, {w.ForecastTemperatureFahrenheit:0}°F, wind {w.WindSpeed} {w.WindDirection}" +
                            (w.ChanceOfPrecipitationPercent is { } rain ? $", {rain}% chance of precipitation." : "."));
                    }
                    else
                    {
                        section.Item().PaddingTop(2).Text("Not available.").FontColor(Muted);
                    }
                    foreach (var alert in data.Alerts.Take(5))
                        section.Item().PaddingTop(2).Text($"NWS {alert.Event} ({alert.Severity}) for {alert.AreaDescription}");
                });

                if (data.Frames.Count > 0)
                {
                    body.Item().Text("Cameras").Bold().FontColor(Accent);
                    body.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); });
                        foreach (var frame in data.Frames)
                        {
                            table.Cell().Padding(3).ShowEntire().Column(cell =>
                            {
                                cell.Item().Image(frame.Image).FitWidth();
                                cell.Item().PaddingTop(2).Text(frame.Caption).FontSize(7.5f).FontColor(Muted);
                            });
                        }
                    });
                }

                foreach (var note in data.Notes)
                    body.Item().Text(note).FontSize(8).Italic().FontColor(Muted);
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Muted));
                text.Span("Public camera, incident and weather data as published by their sources. Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        })).GeneratePdf();
    }
}
