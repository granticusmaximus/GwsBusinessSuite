using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Infrastructure.Services;
using UglyToad.PdfPig;

namespace GwsBusinessSuite.Tests;

public sealed class IncidentReportTests
{
    // A real 2x2 PNG, so the image path through QuestPDF is exercised.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGNgYGD4z8DAwMDAxMDAAAAPEgEB8wKsGQAAAABJRU5ErkJggg==");

    [Fact]
    public void Render_ShouldProduceAPdfWithTheIncidentWeatherAndCameras()
    {
        var at = new DateTimeOffset(2026, 10, 12, 14, 5, 0, TimeSpan.Zero);
        var data = new IncidentReportData(
            new IncidentReportSubject("Crash on I 75", "Crashes", "major", "I 75", "Two lanes blocked", "GDOT", 33.75, -84.39,
                Lanes: "Two lanes blocked", StillListed: "Listed by the source when this report was made."),
            at, "grant", Png, Png,
            new WeatherSnapshot(61, "Light Rain", 64, "Rain Showers", "Rain later", "10 mph", "NW", 70),
            [new WeatherAlert("w1", "Flood Watch", "Moderate", "Fulton", "", [])],
            [new IncidentReportFrame("I-75 at Exit 246", at, Png, "I-75 at Exit 246 · 0.3 mi away · now")],
            ["A note about something missing."]);

        var pdf = IncidentReportService.Render(data);

        pdf.Take(4).Should().Equal("%PDF"u8.ToArray());
        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        text.Should().Contain("OVERWATCH INCIDENT REPORT").And.Contain("Crash on I 75").And.Contain("Two lanes blocked")
            .And.Contain("Now 61°F, Light Rain").And.Contain("70% chance of precipitation").And.Contain("NWS Flood Watch (Moderate) for Fulton")
            .And.Contain("I-75 at Exit 246 · 0.3 mi away · now").And.Contain("A note about something missing.")
            .And.Contain($"Generated {at.ToLocalTime():dd-MM-yy HH:mm} by grant");
        IncidentReportService.FileName(data).Should().Be($"incident-report-{at.ToLocalTime():yyyyMMdd-HHmm}.pdf");
    }

    [Fact]
    public void Render_ShouldStillWork_WithNothingButTheIncident()
    {
        var data = new IncidentReportData(
            new IncidentReportSubject("Incident capture", "Incident capture", "", "North", "Captured.", "Overwatch", 33.75, -84.39),
            DateTimeOffset.UtcNow, "grant", null, null, null, [], [], ["No still-image camera frames were available."]);

        using var document = PdfDocument.Open(IncidentReportService.Render(data));

        string.Join(" ", document.GetPages().Select(p => p.Text)).Should().Contain("Not available.").And.Contain("No still-image camera frames");
    }

    [Fact]
    public void Spread_ShouldKeepTheFirstAndLastAndAnEvenSpread()
    {
        var frames = Enumerable.Range(0, 20).Select(i => new TimelapseFrame($"{i}.jpg", DateTimeOffset.UnixEpoch.AddMinutes(i))).ToList();

        IncidentReportService.Spread(frames, 6).Select(f => f.FileName).Should().Equal("0.jpg", "4.jpg", "8.jpg", "11.jpg", "15.jpg", "19.jpg");
        IncidentReportService.Spread(frames.Take(3).ToList(), 6).Should().HaveCount(3);
    }
}
