using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class CameraConditionsTests
{
    [Fact]
    public void Parse_ShouldReadAWellFormedReading()
    {
        var reading = CameraConditions.Parse("""{"road":"snow","visibility":"fog","traffic":"light","note":"plow on shoulder","answer":"Yes, light snow is falling."}""");

        reading.Road.Should().Be("snow");
        reading.Visibility.Should().Be("fog");
        reading.Traffic.Should().Be("light");
        reading.Note.Should().Be("plow on shoulder");
        reading.Answer.Should().Be("Yes, light snow is falling.");
        reading.Level.Should().Be(CameraConditions.BadLevel);
    }

    [Fact]
    public void Parse_ShouldTolerateProseAroundTheJson_AndNormaliseValues()
    {
        var reading = CameraConditions.Parse("""Sure! Here is the reading: {"road":"Wet","visibility":"misty","traffic":"MODERATE","note":42} Hope that helps.""");

        reading.Road.Should().Be("wet");
        reading.Visibility.Should().Be("unknown", "'misty' isn't one of the allowed values");
        reading.Traffic.Should().Be("moderate");
        reading.Note.Should().BeEmpty();
        reading.Level.Should().Be(CameraConditions.CautionLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I can't see the image.")]
    [InlineData("{not json}")]
    public void Parse_ShouldReturnNoReading_ForUnusableOutput(string output)
    {
        CameraConditions.Parse(output).Level.Should().Be(CameraConditions.UnknownLevel);
    }

    [Theory]
    [InlineData("dry", "clear", "light", "ok")]
    [InlineData("dry", "clear", "stopped", "bad")]
    [InlineData("ice", "clear", "light", "bad")]
    [InlineData("dry", "rain", "light", "caution")]
    [InlineData("dry", "clear", "heavy", "caution")]
    [InlineData("unknown", "unknown", "unknown", "unknown")]
    [InlineData("unknown", "dark", "unknown", "ok")]
    public void LevelOf_ShouldRankTheWorstCondition(string road, string visibility, string traffic, string expected)
    {
        CameraConditions.LevelOf(road, visibility, traffic).Should().Be(expected);
    }

    [Theory]
    [InlineData("llama3.2-vision:11b", true)]
    [InlineData("gemma4:latest", true)]
    [InlineData("qwen2.5vl:7b", true)]
    [InlineData("llama3.2:latest", false)]
    [InlineData("qwen2.5-coder:7b", false)]
    public void LooksVisionCapable_ShouldRecogniseCommonVisionModels(string model, bool expected)
    {
        CameraConditions.LooksVisionCapable(model).Should().Be(expected);
    }

    [Fact]
    public void UserPrompt_ShouldCarryTheQuestion_WhenThereIsOne()
    {
        CameraConditions.UserPrompt("  Is it snowing?  ").Should().EndWith("The user's question: Is it snowing?");
        CameraConditions.UserPrompt(null).Should().Be("Classify this camera image.");
    }

    [Fact]
    public async Task Downloader_ShouldReturnRealImages_AndRefuseEverythingElse()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        var downloader = new CameraSnapshotDownloader(new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/cam.jpg" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) },
            "/spa.jpg" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>retired</html>") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        })));

        (await downloader.DownloadAsync("https://cams.test/cam.jpg")).Should().Equal(jpeg);
        (await downloader.DownloadAsync("https://cams.test/spa.jpg")).Should().BeNull("an HTML page isn't a frame");
        (await downloader.DownloadAsync("https://cams.test/missing.jpg")).Should().BeNull();
        (await downloader.DownloadAsync("http://cams.test/cam.jpg")).Should().BeNull("only HTTPS frames are fetched");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
