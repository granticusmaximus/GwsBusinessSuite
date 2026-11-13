using FluentAssertions;
using GwsBusinessSuite.Application.Briefings;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Overwatch briefing PDF (13-11-26).
public sealed class OverwatchViewBriefingPdfTests
{
    [Fact]
    public void Render_ShouldMakeAPdf_WithOrWithoutTheMapAndFacts()
    {
        var bounds = new BoundingBox(34, 33, -84, -85);
        var full = OverwatchViewBriefingPdf.Render(new ViewBriefingData(bounds, DateTimeOffset.Now, "grant",
            null, [("Hazards", "2 earthquakes"), ("Ships", "on")],
            new BriefingFacts("this view", DateTimeOffset.Now, [new BriefingFact(1, "hazard", "M4.2 earthquake near Atlanta")]),
            new BriefingText("A small earthquake was felt [1].", [1])));
        var bare = OverwatchViewBriefingPdf.Render(new ViewBriefingData(bounds, DateTimeOffset.Now, "grant", null, [], null, null));

        System.Text.Encoding.ASCII.GetString(full, 0, 5).Should().Be("%PDF-");
        System.Text.Encoding.ASCII.GetString(bare, 0, 5).Should().Be("%PDF-");
        full.Length.Should().BeGreaterThan(bare.Length);
    }
}
