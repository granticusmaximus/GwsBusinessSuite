using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.WorldNewsKit;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace GwsBusinessSuite.Infrastructure.Services;

// N14 (14-11-26): a story's dossier as a PDF: the story and its label, what the desk pinned
// (reports, official records, notes, links) with who pinned each and when, the desk's notes, the
// verification checklist and the sign-offs. Same look as the other World News PDFs.
// The evidence pack's extra parts (19-11-26): the timeline, the official signals near the story,
// and the pack's number in the ledger. Null renders the plain dossier.
public sealed record EvidenceExtras(IReadOnlyList<TimelineEntry> Timeline, EvidenceBoard? Board, long PackNumber);

public static class DossierPdf
{
    private const string BodyFont = "Liberation Sans";
    private const string Ink = "#1F2933";
    private const string Muted = "#6B7280";
    private const string Accent = "#8F5A00";
    private const string Rule = "#D1D5DB";

    static DossierPdf() => QuestPdfSetup.Apply();

    public static string FileName(StoryDocument story) =>
        $"dossier-{story.Event.FirstSeen.ToLocalTime():yyyyMMdd}-{story.Event.Id[..Math.Min(8, story.Event.Id.Length)]}.pdf";

    public static byte[] Render(StoryDocument story, IReadOnlyList<DossierItem> items, IReadOnlyList<DeskNote> notes, IReadOnlyList<DeskCheck> checks,
        IReadOnlyList<SignOff> signOffs, string generatedBy, DateTimeOffset now, EvidenceExtras? extras = null)
    {
        var title = story.Event.Headline ?? story.Event.Title;
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(9.5f).FontColor(Ink));
            page.Header().Column(header =>
            {
                header.Item().Text("STORY DOSSIER").FontSize(8).Bold().FontColor(Accent).LetterSpacing(0.08f);
                header.Item().PaddingTop(2).Text(title).FontSize(15).Bold();
                header.Item().PaddingTop(2).Text($"Compiled {now.ToLocalGwsDateTime()} by {generatedBy} · first reported {story.Event.FirstSeen.ToLocalGwsDateTime()}"
                    + (story.Trust is { } t ? $" · {t.Confidence.Label}: {t.Confidence.Reason}" : string.Empty)).FontSize(8.5f).FontColor(Muted);
                header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Rule);
            });
            page.Content().PaddingTop(10).Column(body =>
            {
                body.Spacing(10);
                foreach (var kind in Dossiers.Kinds)
                {
                    var ofKind = items.Where(i => i.Kind == kind).ToList();
                    if (ofKind.Count == 0) continue;
                    body.Item().Column(section =>
                    {
                        section.Item().Text(KindHeading(kind)).Bold().FontColor(Accent);
                        foreach (var item in ofKind)
                        {
                            section.Item().PaddingTop(3).Text(t =>
                            {
                                t.Span(item.Title).Bold();
                                if (item.Detail.Length > 0) t.Span(" · " + item.Detail);
                                t.Span($" ({item.AddedBy}, {item.At.ToLocalGwsDateTime()})").FontColor(Muted).FontSize(8);
                            });
                            if (item.Url is { } url) section.Item().Text(url).FontSize(8).FontColor(Muted);
                        }
                    });
                }
                if (items.Count == 0) body.Item().Text("Nothing has been pinned to this dossier yet.").FontColor(Muted);

                if (notes.Count > 0)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text("Desk notes").Bold().FontColor(Accent);
                        foreach (var note in notes.OrderBy(n => n.CreatedAt))
                            section.Item().PaddingTop(3).Text(t => { t.Span($"{note.CreatedAt.ToLocalGwsDateTime()} {note.Username}: ").FontColor(Muted).FontSize(8); t.Span(note.Text); });
                    });
                }

                body.Item().Column(section =>
                {
                    section.Item().Text("Verification checklist").Bold().FontColor(Accent);
                    foreach (var (key, label, _) in DeskChecklist.Items)
                    {
                        var check = checks.FirstOrDefault(c => c.Item == key);
                        section.Item().PaddingTop(2).Text(check is { Done: true }
                            ? $"[x] {label}: {check.Evidence} ({check.CheckedBy}, {(check.CheckedAt is { } at ? at.ToLocalGwsDateTime() : "")})"
                            : $"[ ] {label}: not checked yet");
                    }
                });

                if (signOffs.Count > 0)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text("Sign-offs").Bold().FontColor(Accent);
                        foreach (var s in signOffs)
                            section.Item().PaddingTop(2).Text($"{s.Kind}: {s.Username}, {s.At.ToLocalGwsDateTime()} (label then: {StoryTimeline.LevelText(s.LabelLevel)}; record {s.Hash[..12]})");
                    });
                }

                if (extras is not null)
                {
                    if (story.Trust is { } trust)
                        body.Item().Column(section =>
                        {
                            section.Item().Text("Independent sources").Bold().FontColor(Accent);
                            section.Item().PaddingTop(2).Text($"{trust.Independence.Outlets} outlets, {trust.Independence.Independent} independent: " +
                                string.Join(", ", trust.Independence.Origins.Take(12).Select(o => o.Name)) + ". Wire copies and outlets with one owner count once.");
                        });
                    if (extras.Board is { Signals.Count: > 0 } board)
                        body.Item().Column(section =>
                        {
                            section.Item().Text("Official signals near the story").Bold().FontColor(Accent);
                            foreach (var signal in board.Signals.Take(20))
                                section.Item().PaddingTop(2).Text(t =>
                                {
                                    t.Span($"{signal.Source}: {signal.Title} · {signal.Kilometres:0} km");
                                    if (signal.At is { } at) t.Span($" · {at.ToLocalGwsDateTime()}").FontColor(Muted);
                                });
                        });
                    if (extras.Timeline.Count > 0)
                        body.Item().Column(section =>
                        {
                            section.Item().Text("Timeline").Bold().FontColor(Accent);
                            foreach (var entry in extras.Timeline.OrderBy(e => e.At).Take(40))
                                section.Item().PaddingTop(2).Text(t => { t.Span(entry.At.ToLocalGwsDateTime() + "  ").FontColor(Muted); t.Span($"{entry.Kind}: {entry.Text}"); });
                        });
                    if (story.Summary is { } summary)
                        body.Item().Column(section =>
                        {
                            section.Item().Text("Summary (written by SentinelGPT, an AI: check it against the sources)").Bold().FontColor(Accent);
                            section.Item().PaddingTop(2).Text(summary.Text);
                        });
                }

                body.Item().Column(section =>
                {
                    section.Item().Text("Sources").Bold().FontColor(Accent);
                    foreach (var source in story.Sources.Take(40))
                        section.Item().PaddingTop(2).Text(t => { t.Span($"{source.Report.Outlet}: {source.Report.Title}"); t.Span($" · {source.Report.Url}").FontSize(8).FontColor(Muted); });
                });
            });
            page.Footer().AlignRight().Text(t =>
            {
                if (extras is not null)
                    t.Span($"Evidence pack {extras.PackNumber} · its SHA-256 is recorded in the World News ledger on the story's desk · ").FontSize(7.5f).FontColor(Muted);
                t.Span("Page ").FontSize(7.5f).FontColor(Muted);
                t.CurrentPageNumber().FontSize(7.5f).FontColor(Muted);
            });
        })).GeneratePdf();
    }

    private static string KindHeading(string kind) => kind switch
    {
        Dossiers.Report => "Pinned reports",
        Dossiers.Record => "Official records and sensor signals",
        Dossiers.Note => "Pinned notes",
        _ => "Links",
    };
}
