using FluentAssertions;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Services;
using UglyToad.PdfPig;

namespace GwsBusinessSuite.Tests;

// Invoice PDFs (27-10-26): any invoice, whatever its status, read back as text.
public sealed class InvoicePdfTests
{
    private static readonly Guid Id = new("677ed449-0385-4b8b-8c77-71bc69c0af7f");
    private static readonly BillingIssuer Issuer = new("GWS Studio", "billing@example.com", "1 Main Street\nSpringfield", "TX-123", "Bank: Example Bank\nAccount 0001");

    private static InvoiceView Invoice(string status, string? hostedUrl = null, DateTimeOffset? paidAt = null) => new(
        Id, Guid.NewGuid(), "Dana Rivers", null, "October retainer", status, "eur",
        new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), status == InvoiceStatuses.Draft ? null : new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
        paidAt, "<p>Thanks for the <strong>work</strong> &amp; patience.</p><ul><li>Net 14</li></ul>", hostedUrl,
        [new InvoiceLineItemView(Guid.NewGuid(), "Design sprint", 2, 1250.5m), new InvoiceLineItemView(Guid.NewGuid(), "Hosting", 1, 40m)],
        new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));

    // The status stamp is drawn rotated, so word extraction splits it; its letters are the only
    // ones set this large.
    private static string Stamp(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Concat(document.GetPage(1).Letters.Where(letter => letter.PointSize > 60).Select(letter => letter.Value));
    }

    private static string Text(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPages().Select(page => string.Join(" ", page.GetWords().Select(word => word.Text))));
    }

    [Fact]
    public void Draft_ShouldShowWhoItsFromAndTo_TheLines_TheTotal_AndADraftStamp()
    {
        var pdf = InvoicePdf.Render(new InvoicePdfData(Invoice(InvoiceStatuses.Draft), Issuer, "Dana Rivers", "Rivers & Co", "dana@example.com"));

        pdf.Take(4).Should().Equal("%PDF"u8.ToArray());
        var text = Text(pdf);
        text.Should().Contain("GWS Studio").And.Contain("Springfield").And.Contain("TX-123");
        text.Should().Contain("INV-677ED449");
        text.Should().Contain("Dana Rivers").And.Contain("Rivers & Co").And.Contain("dana@example.com");
        text.Should().Contain("Design sprint").And.Contain("EUR 2,501.00").And.Contain("EUR 2,541.00");
        text.Should().Contain("10-11-26", "a draft's due date is the stored date, dd-MM-yy");
        Stamp(pdf).Should().Be("DRAFT");
        text.Should().Contain("Thanks for the work & patience.").And.Contain("Net 14", "notes are the editor's HTML as plain text");
        text.Should().Contain("Account 0001");
        text.Should().NotContain("<p>");
    }

    [Fact]
    public void Sent_ShouldCarryThePayLink_AndPaid_ShouldSayWhen()
    {
        var sent = InvoicePdf.Render(new InvoicePdfData(Invoice(InvoiceStatuses.Sent, "https://invoice.stripe.com/i/abc"), Issuer, "Dana Rivers", null, null));
        Text(sent).Should().Contain("https://invoice.stripe.com/i/abc");
        Stamp(sent).Should().BeEmpty("a sent invoice is the real thing");
        Text(InvoicePdf.Render(new InvoicePdfData(Invoice(InvoiceStatuses.Paid, paidAt: new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)), Issuer, "Dana Rivers", null, null)))
            .Should().Contain("Paid in full on 25-10-26");
    }

    [Fact]
    public void WithoutIssuerDetails_ShouldStillRender_AndNameTheFileFromTheTitle()
    {
        var invoice = Invoice(InvoiceStatuses.Void);
        var pdf = InvoicePdf.Render(new InvoicePdfData(invoice, BillingIssuer.Empty, "Dana Rivers", null, null));
        Stamp(pdf).Should().Be("VOID");
        Text(pdf).Should().Contain("Dana Rivers").And.StartWith("INVOICE", "no From block without the business's details");
        InvoicePdf.FileName(invoice).Should().Be("INV-677ED449-october-retainer.pdf");
        InvoicePdf.PlainText("<h2>Terms</h2><p>Line one<br>Line two</p>").Should().Be("Terms\nLine one\nLine two");
    }
}
