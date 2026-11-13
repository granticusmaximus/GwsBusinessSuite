using System.Globalization;
using GwsBusinessSuite.Application.Briefings;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace GwsBusinessSuite.Infrastructure.Services;

// Overwatch briefing export (13-11-26): the current view as a PDF: a satellite map of it, every layer
// that's on with what it shows, and the "Brief me" facts (and SentinelGPT's write-up of them, marked
// as AI) when they've been gathered. Same look as the incident report.
public sealed record ViewBriefingData(
    BoundingBox Bounds,
    DateTimeOffset GeneratedAt,
    string GeneratedBy,
    byte[]? Map,
    IReadOnlyList<(string Layer, string Shows)> Layers,
    BriefingFacts? Facts,
    BriefingText? Text);

public sealed class OverwatchViewBriefingPdf(HttpClient http, ILogger<OverwatchViewBriefingPdf> logger)
{
    private const string MapServer = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/export";
    private const string BodyFont = "Liberation Sans";
    private const string Ink = "#1F2933";
    private const string Muted = "#6B7280";
    private const string Accent = "#0B6E4F";
    private const string Rule = "#D1D5DB";

    static OverwatchViewBriefingPdf() => QuestPdfSetup.Apply();

    public async Task<byte[]> BuildAsync(BoundingBox bounds, string generatedBy, IReadOnlyList<(string Layer, string Shows)> layers,
        BriefingFacts? facts, BriefingText? text, DateTimeOffset now, CancellationToken ct = default) =>
        Render(new ViewBriefingData(bounds, now, generatedBy, await MapAsync(bounds, ct), layers, facts, text));

    public static string FileName(ViewBriefingData data) =>
        $"overwatch-briefing-{data.GeneratedAt.ToLocalTime():yyyy-MM-dd-HHmm}.pdf";

    private async Task<byte[]?> MapAsync(BoundingBox b, CancellationToken ct)
    {
        // Clamp to the map's limits; a view across the date line exports its eastern part.
        var west = Math.Max(-180, b.West);
        var east = b.East > west ? Math.Min(180, b.East) : 180;
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{MapServer}?bbox={west},{Math.Max(-85, b.South)},{east},{Math.Min(85, b.North)}&bboxSR=4326&imageSR=3857&size=1000,620&format=jpg&f=image");
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 1000 ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Overwatch briefing: map export failed");
            return null;
        }
    }

    public static byte[] Render(ViewBriefingData data)
    {
        var b = data.Bounds;
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(9.5f).FontColor(Ink));
            page.Header().Column(header =>
            {
                header.Item().Text("OVERWATCH BRIEFING").FontSize(8).Bold().FontColor(Accent).LetterSpacing(0.08f);
                header.Item().PaddingTop(2).Text(string.Create(CultureInfo.InvariantCulture,
                    $"View {b.South:0.##}° to {b.North:0.##}° N, {b.West:0.##}° to {b.East:0.##}° E")).FontSize(15).Bold();
                header.Item().PaddingTop(2).Text($"Generated {data.GeneratedAt.ToLocalGwsDateTime()} by {data.GeneratedBy}").FontSize(8.5f).FontColor(Muted);
                header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Rule);
            });
            page.Content().PaddingTop(10).Column(body =>
            {
                body.Spacing(10);
                if (data.Map is { } map)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Image(map).FitWidth();
                        section.Item().PaddingTop(2).Text("Imagery: Esri World Imagery.").FontSize(7.5f).FontColor(Muted);
                    });
                }
                body.Item().Column(section =>
                {
                    section.Item().Text("Layers on").Bold().FontColor(Accent);
                    if (data.Layers.Count == 0) section.Item().PaddingTop(2).Text("Only cameras (no other layer was on).").FontColor(Muted);
                    foreach (var (layer, shows) in data.Layers)
                        section.Item().PaddingTop(2).Text(t => { t.Span(layer + ": ").Bold(); t.Span(shows); });
                });
                if (data.Facts is { Facts.Count: > 0 } facts)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text($"What's happening ({facts.Facts.Count} facts, {facts.At.ToLocalGwsDateTime()})").Bold().FontColor(Accent);
                        foreach (var fact in facts.Facts)
                            section.Item().PaddingTop(2).Text($"[{fact.Number}] {fact.Kind}: {fact.Text}");
                    });
                }
                if (data.Text is { Text.Length: > 0 } text)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text("Summary (written by SentinelGPT from the facts above; check the cited facts)").Bold().FontColor(Accent);
                        section.Item().PaddingTop(2).Text(text.Text);
                    });
                }
            });
            page.Footer().AlignRight().Text(t =>
            {
                t.Span("Page ").FontSize(7.5f).FontColor(Muted);
                t.CurrentPageNumber().FontSize(7.5f).FontColor(Muted);
            });
        })).GeneratePdf();
    }
}
