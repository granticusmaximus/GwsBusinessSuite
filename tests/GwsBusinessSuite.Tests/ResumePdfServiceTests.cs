using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class ResumePdfServiceTests
{
    [Fact]
    public void GenerateResumePdf_ShouldProduceAValidNonEmptyPdf()
    {
        var service = new ResumePdfService();

        var bytes = service.GenerateResumePdf();

        bytes.Should().NotBeEmpty();
        // Every PDF file starts with this magic header regardless of content/version.
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void GenerateResumePdf_ShouldBeDeterministicInSize_AcrossRepeatedCalls()
    {
        // ResumeContent is fixed/hardcoded, so regenerating shouldn't meaningfully change
        // output size run to run (guards against e.g. accidentally embedding a timestamp
        // or random identifier that would make every download subtly different).
        var service = new ResumePdfService();

        var first = service.GenerateResumePdf();
        var second = service.GenerateResumePdf();

        Math.Abs(first.Length - second.Length).Should().BeLessThan(50);
    }

    [Fact]
    public void GenerateResumePdf_ShouldNotThrow_WhenCalledMultipleTimesInARow()
    {
        // QuestPDF's Community license is set once in a static constructor - this
        // guards against any static/shared state issue across repeated calls (e.g. from
        // concurrent requests to /resume.pdf).
        var service = new ResumePdfService();

        var act = () =>
        {
            service.GenerateResumePdf();
            service.GenerateResumePdf();
            service.GenerateResumePdf();
        };

        act.Should().NotThrow();
    }
}
