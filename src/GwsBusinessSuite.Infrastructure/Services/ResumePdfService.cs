using GwsBusinessSuite.Application.Resume;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GwsBusinessSuite.Infrastructure.Services;

// Renders ResumeContent as a single-page, agency-style PDF via QuestPDF. Uses "Liberation
// Sans"/"Liberation Serif" rather than Arial/Georgia because the production container
// (see Dockerfile) only installs the `fonts-liberation` package - any other family name
// would silently fall back to a missing-glyph substitute there.
public sealed class ResumePdfService : IResumePdfService
{
    private const string HeadingFont = "Liberation Serif";
    private const string BodyFont = "Liberation Sans";
    private const string AccentHex = "#B45309";
    private const string DarkHex = "#141210";
    private const string TextHex = "#27272A";
    private const string MutedHex = "#6B7280";
    private const string RuleHex = "#E5E7EB";

    static ResumePdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerateResumePdf()
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(10).FontColor(TextHex));

                page.Content().Column(column =>
                {
                    column.Item().Background(DarkHex).Padding(24).Column(header =>
                    {
                        header.Item().Text(ResumeContent.FullName)
                            .FontFamily(HeadingFont).FontSize(27).Bold().FontColor(Colors.White);
                        header.Item().PaddingTop(3).Text(ResumeContent.Title)
                            .FontSize(12.5f).FontColor(AccentHex);
                        header.Item().PaddingTop(10).Text(text =>
                        {
                            text.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Colors.Grey.Lighten2));
                            text.Span(ResumeContent.Email);
                            text.Span("    ·    ").FontColor(Colors.Grey.Darken1);
                            text.Span(ResumeContent.Phone);
                            text.Span("    ·    ").FontColor(Colors.Grey.Darken1);
                            text.Span(ResumeContent.Location);
                            text.Span("    ·    ").FontColor(Colors.Grey.Darken1);
                            text.Span(StripScheme(ResumeContent.LinkedInUrl));
                            text.Span("    ·    ").FontColor(Colors.Grey.Darken1);
                            text.Span(StripScheme(ResumeContent.GitHubUrl));
                        });
                    });

                    column.Item().Padding(26).Column(body =>
                    {
                        Section(body, "Summary");
                        body.Item().PaddingTop(3).PaddingBottom(12)
                            .Text(ResumeContent.Summary).LineHeight(1.35f);

                        Section(body, "Experience");
                        foreach (var job in ResumeContent.Experience)
                        {
                            body.Item().PaddingTop(8).BorderLeft(2).BorderColor(AccentHex)
                                .PaddingLeft(14).Column(entry =>
                            {
                                entry.Item().Row(row =>
                                {
                                    row.RelativeItem().Text(text =>
                                    {
                                        text.Span(job.Title).Bold().FontSize(11).FontColor(TextHex);
                                        text.Span("  —  " + job.Company).FontColor(MutedHex).FontSize(10);
                                    });
                                    row.ConstantItem(130).AlignRight()
                                        .Text(job.DateRange).FontSize(8.5f).FontColor(MutedHex);
                                });

                                foreach (var bullet in job.Bullets)
                                {
                                    entry.Item().PaddingTop(2).Row(bulletRow =>
                                    {
                                        bulletRow.ConstantItem(10).Text("•").FontColor(AccentHex);
                                        bulletRow.RelativeItem().Text(bullet).FontSize(9).LineHeight(1.25f);
                                    });
                                }

                                if (job.Technologies.Count > 0)
                                {
                                    entry.Item().PaddingTop(3)
                                        .Text(string.Join("   ", job.Technologies))
                                        .FontSize(8).Italic().FontColor(MutedHex);
                                }
                            });
                        }

                        Section(body, "Education", topPadding: 12);
                        body.Item().PaddingTop(5).Text(text =>
                        {
                            text.Span(ResumeContent.Education.Degree).Bold().FontSize(10).FontColor(TextHex);
                            text.Span("  —  " + ResumeContent.Education.School +
                                       " (" + ResumeContent.Education.DateLabel + ")")
                                .FontColor(MutedHex).FontSize(9.5f);
                        });

                        Section(body, "Skills", topPadding: 12);
                        body.Item().PaddingTop(5).Text(text =>
                        {
                            text.DefaultTextStyle(x => x.FontSize(9.5f));
                            for (var i = 0; i < ResumeContent.Skills.Count; i++)
                            {
                                text.Span(ResumeContent.Skills[i]);
                                if (i < ResumeContent.Skills.Count - 1)
                                {
                                    text.Span("   ·   ").FontColor(RuleHex);
                                }
                            }
                        });
                    });
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void Section(QuestPDF.Fluent.ColumnDescriptor column, string title, float topPadding = 0)
    {
        var item = topPadding > 0 ? column.Item().PaddingTop(topPadding) : column.Item();
        item.Text(title.ToUpperInvariant())
            .FontFamily(HeadingFont).FontSize(12.5f).Bold().FontColor(AccentHex);
    }

    private static string StripScheme(string url) =>
        url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
}
