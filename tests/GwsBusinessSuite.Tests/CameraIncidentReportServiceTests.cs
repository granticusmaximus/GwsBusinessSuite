using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class CameraIncidentReportServiceTests
{
    private static readonly CameraFeed Camera = new("gdot-1", "GDOT: I-75 @ SR 96", 32.5123456, -83.71, "https://cams.example/1.jpg",
        CameraStreamKind.Snapshot, "GDOT", "https://511ga.org");

    [Fact]
    public void ReportMarkdown_ShouldCarryTheFrameLocationTimeAndNotes()
    {
        var md = CameraIncidentReportService.ReportMarkdown(Camera, new DateTimeOffset(2026, 10, 8, 14, 5, 0, TimeSpan.Zero),
            "/media/abc", null, "Vehicle stopped in the right lane.", "grant");

        md.Should().StartWith("![GDOT: I-75 @ SR 96](/media/abc)");
        md.Should().Contain("**Location:** 32.51235, -83.71");
        md.Should().Contain("https://www.openstreetmap.org/?mlat=32.51235&mlon=-83.71");
        md.Should().Contain("(2026-10-08 14:05:00Z)");
        md.Should().Contain("Vehicle stopped in the right lane.");
        md.Should().Contain("[GDOT](https://511ga.org)");

        var blocks = WikiBlockJson.FromMarkdown(md);
        blocks.Should().Contain(b => b.Type == WikiBlockTypes.Image && b.Props["url"] == "/media/abc",
            "the frame must become a real Sentinel image block, not literal text");
    }

    [Fact]
    public void ReportMarkdown_ShouldExplainAMissingImageAndAnalysis()
    {
        var md = CameraIncidentReportService.ReportMarkdown(Camera with { Name = "Cam *bold* [x]" }, DateTimeOffset.UtcNow,
            null, "Live video camera - no still frame to save.", null, "grant");

        md.Should().StartWith("_Live video camera - no still frame to save._");
        md.Should().Contain("No analysis was run before saving");
        md.Should().Contain("Cam \\*bold\\* \\[x\\]", "camera names are escaped so they don't turn into formatting");
    }
}
