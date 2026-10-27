using System.Net;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GwsBusinessSuite.Infrastructure.Services;

// An invoice as a PDF to download, in any status. Draft and Void are stamped across the page so
// neither passes for a live invoice; a Sent one carries its Stripe payment link and a Paid one
// says when it was paid. Liberation Sans because it's the font the production container installs
// (see ResumePdfService).
public static partial class InvoicePdf
{
    private const string BodyFont = "Liberation Sans";
    private const string Ink = "#1F2933";
    private const string Muted = "#6B7280";
    private const string Accent = "#B45309";
    private const string Rule = "#E5E7EB";
    private const string Paid = "#15803D";

    static InvoicePdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // A glyph the font lacks (a name in another script, say) prints blank rather than failing the download.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    // A short, stable reference for an invoice the app made (Stripe numbers its own once sent).
    public static string Reference(Guid invoiceId) => "INV-" + invoiceId.ToString("N")[..8].ToUpperInvariant();

    public static string FileName(InvoiceView invoice)
    {
        var slug = NonWordRun().Replace(invoice.Title.ToLowerInvariant(), "-").Trim('-');
        return $"{Reference(invoice.Id)}{(slug.Length == 0 ? "" : "-" + slug[..Math.Min(slug.Length, 40)].TrimEnd('-'))}.pdf";
    }

    public static byte[] Render(InvoicePdfData data)
    {
        var invoice = data.Invoice;
        var issuer = data.Issuer;
        var stamp = invoice.Status switch
        {
            InvoiceStatuses.Draft => "DRAFT",
            InvoiceStatuses.Void => "VOID",
            _ => null
        };
        // A draft's due date is stored as a date at UTC midnight; a sent invoice's is the moment
        // Stripe set, shown in local time like every other date.
        var due = invoice.DueDate is not { } dueDate ? null
            : invoice.Status == InvoiceStatuses.Draft ? DateOnly.FromDateTime(dueDate.UtcDateTime).ToGwsDate()
            : dueDate.ToLocalGwsDate();

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(10).FontColor(Ink));

            if (stamp is not null)
            {
                page.Background().AlignCenter().AlignMiddle().Rotate(-30)
                    .Text(stamp).FontSize(110).Bold().FontColor(invoice.Status == InvoiceStatuses.Void ? "#FECACA" : "#E5E7EB");
            }

            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    row.RelativeItem().Column(from =>
                    {
                        if (issuer.IsEmpty) return;
                        from.Item().Text(issuer.BusinessName).FontSize(16).Bold();
                        foreach (var line in Lines(issuer.Address)) from.Item().Text(line).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(issuer.Email)) from.Item().Text(issuer.Email).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(issuer.TaxId)) from.Item().Text($"Tax ID {issuer.TaxId}").FontColor(Muted);
                    });
                    row.ConstantItem(190).Column(facts =>
                    {
                        facts.Item().AlignRight().Text("INVOICE").FontSize(22).Bold().FontColor(Accent).LetterSpacing(0.06f);
                        facts.Item().AlignRight().Text(Reference(invoice.Id)).FontColor(Muted);
                        facts.Item().PaddingTop(6).AlignRight().Text(text =>
                        {
                            text.Span("Issued ").FontColor(Muted);
                            text.Span((invoice.SentAt ?? invoice.CreatedAt).ToLocalGwsDate());
                        });
                        if (due is not null)
                        {
                            facts.Item().AlignRight().Text(text =>
                            {
                                text.Span("Due ").FontColor(Muted);
                                text.Span(due).Bold();
                            });
                        }
                        facts.Item().AlignRight().Text(text =>
                        {
                            text.Span("Status ").FontColor(Muted);
                            text.Span(invoice.Status);
                        });
                    });
                });
                header.Item().PaddingTop(14).LineHorizontal(1).LineColor(Rule);
            });

            page.Content().PaddingTop(16).Column(body =>
            {
                body.Spacing(14);
                body.Item().Column(to =>
                {
                    to.Item().Text("BILL TO").FontSize(8).Bold().FontColor(Muted).LetterSpacing(0.08f);
                    to.Item().PaddingTop(2).Text(data.ClientName).Bold();
                    if (!string.IsNullOrWhiteSpace(data.ClientCompany)) to.Item().Text(data.ClientCompany);
                    if (!string.IsNullOrWhiteSpace(data.ClientEmail)) to.Item().Text(data.ClientEmail).FontColor(Muted);
                });

                body.Item().Text(invoice.Title).FontSize(13).Bold();

                body.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.ConstantColumn(44);
                        columns.ConstantColumn(96);
                        columns.ConstantColumn(96);
                    });
                    table.Header(head =>
                    {
                        static IContainer Heading(IContainer cell) => cell.BorderBottom(1).BorderColor(Ink).PaddingVertical(4);
                        head.Cell().Element(Heading).Text("Description").Bold();
                        head.Cell().Element(Heading).AlignRight().Text("Qty").Bold();
                        head.Cell().Element(Heading).AlignRight().Text("Unit price").Bold();
                        head.Cell().Element(Heading).AlignRight().Text("Amount").Bold();
                    });
                    static IContainer Line(IContainer cell) => cell.BorderBottom(1).BorderColor(Rule).PaddingVertical(5);
                    foreach (var item in invoice.LineItems)
                    {
                        table.Cell().Element(Line).Text(item.Description);
                        table.Cell().Element(Line).AlignRight().Text(item.Quantity.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
                        table.Cell().Element(Line).AlignRight().Text(InvoiceCurrencies.Format(item.UnitPriceUsd, invoice.Currency));
                        table.Cell().Element(Line).AlignRight().Text(InvoiceCurrencies.Format(item.TotalUsd, invoice.Currency));
                    }
                    if (invoice.LineItems.Count == 0)
                    {
                        table.Cell().ColumnSpan(4).Element(Line).Text("No line items yet.").FontColor(Muted);
                    }
                });

                body.Item().AlignRight().Text(text =>
                {
                    text.Span("Total  ").FontSize(12).FontColor(Muted);
                    text.Span(invoice.TotalText).FontSize(14).Bold();
                });

                if (invoice.Status == InvoiceStatuses.Paid && invoice.PaidAt is { } paidAt)
                {
                    body.Item().AlignRight().Text($"Paid in full on {paidAt.ToLocalGwsDate()}. Thank you.").Bold().FontColor(Paid);
                }
                else if (invoice.Status == InvoiceStatuses.Sent && !string.IsNullOrWhiteSpace(invoice.StripeHostedInvoiceUrl))
                {
                    body.Item().Column(pay =>
                    {
                        pay.Item().Text("Pay online").Bold();
                        pay.Item().Hyperlink(invoice.StripeHostedInvoiceUrl).Text(invoice.StripeHostedInvoiceUrl).FontColor(Accent).Underline();
                    });
                }

                var notes = PlainText(invoice.Notes);
                if (notes.Length > 0)
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text("Notes").Bold();
                        section.Item().PaddingTop(2).Text(notes);
                    });
                }

                if (!string.IsNullOrWhiteSpace(issuer.PaymentNotes))
                {
                    body.Item().Column(section =>
                    {
                        section.Item().Text("Payment").Bold();
                        section.Item().PaddingTop(2).Text(issuer.PaymentNotes.Trim());
                    });
                }
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text(issuer.IsEmpty ? Reference(invoice.Id) : $"{issuer.BusinessName} · {Reference(invoice.Id)}").FontSize(8).FontColor(Muted);
                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // The editor's notes are HTML: paragraphs, line breaks and list items become lines, the rest
    // of the markup goes, and entities are decoded.
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = BlockEnd().Replace(html, "\n");
        text = ListItem().Replace(text, "• ");
        text = WebUtility.HtmlDecode(Tag().Replace(text, string.Empty));
        return string.Join('\n', text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    [GeneratedRegex(@"<br\s*/?>|</(p|div|h[1-6]|li|blockquote|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWordRun();
}
