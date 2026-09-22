using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Tests;

public sealed class SharedGridViewCodecTests
{
    private static readonly CameraFeed TestCamera = new(
        "gdot-123", "I-85 @ Exit 12", 33.75, -84.39,
        "https://511ga.org/cameras/gdot-123.jpg", CameraStreamKind.Snapshot,
        "GDOT", "https://511ga.org");

    [Fact]
    public void EncodeThenTryDecode_ShouldRoundTripEveryField()
    {
        var view = new SharedGridView(33.75, -84.39, 400_000, [TestCamera]);

        var token = SharedGridViewCodec.Encode(view);
        var decoded = SharedGridViewCodec.TryDecode(token);

        decoded.Should().NotBeNull();
        decoded!.Latitude.Should().Be(view.Latitude);
        decoded.Longitude.Should().Be(view.Longitude);
        decoded.HeightMeters.Should().Be(view.HeightMeters);
        decoded.OpenCameras.Should().ContainSingle();
        decoded.OpenCameras[0].Id.Should().Be(TestCamera.Id);
        decoded.OpenCameras[0].Name.Should().Be(TestCamera.Name);
        decoded.OpenCameras[0].StreamKind.Should().Be(CameraStreamKind.Snapshot);
    }

    [Fact]
    public void EncodeThenTryDecode_ShouldRoundTripWithNoOpenCameras()
    {
        var view = new SharedGridView(47.6, -122.3, 150_000, []);

        var decoded = SharedGridViewCodec.TryDecode(SharedGridViewCodec.Encode(view));

        decoded.Should().NotBeNull();
        decoded!.OpenCameras.Should().BeEmpty();
    }

    [Fact]
    public void Encode_ShouldProduceATokenThatNeedsNoFurtherUrlEscaping()
    {
        var view = new SharedGridView(33.75, -84.39, 400_000, [TestCamera, TestCamera with { Id = "gdot-456" }]);

        var token = SharedGridViewCodec.Encode(view);

        token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-valid-base64!!!")]
    [InlineData("aGVsbG8")] // valid base64url, but not JSON at all
    public void TryDecode_ShouldReturnNull_ForGarbageInput_RatherThanThrow(string? garbage)
    {
        var act = () => SharedGridViewCodec.TryDecode(garbage);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public void TryDecode_ShouldReturnNull_ForOutOfRangeCoordinates(double lat, double lon)
    {
        var token = SharedGridViewCodec.Encode(new SharedGridView(lat, lon, 100_000, []));

        SharedGridViewCodec.TryDecode(token).Should().BeNull();
    }

    [Fact]
    public void TryDecode_ShouldReturnNull_WhenHeightIsNotPositive()
    {
        var token = SharedGridViewCodec.Encode(new SharedGridView(0, 0, 0, []));

        SharedGridViewCodec.TryDecode(token).Should().BeNull();
    }

    [Fact]
    public void TryDecode_ShouldReturnNull_WhenTooManyCamerasAreEncoded()
    {
        var tooMany = Enumerable.Range(0, 10).Select(i => TestCamera with { Id = $"gdot-{i}" }).ToList();
        var token = SharedGridViewCodec.Encode(new SharedGridView(33.75, -84.39, 400_000, tooMany));

        SharedGridViewCodec.TryDecode(token).Should().BeNull();
    }
}
