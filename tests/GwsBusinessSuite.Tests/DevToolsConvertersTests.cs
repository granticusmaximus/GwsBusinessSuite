using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

public sealed class DevToolsConvertersTests
{
    [Fact]
    public void UnixTimestamp_ShouldRoundTripThroughDateTime()
    {
        var formatted = DevToolsConverters.UnixTimestampToDateTime(0);

        formatted.Success.Should().BeTrue();
        formatted.Output.Should().Contain("1970-01-01");
        DevToolsConverters.DateTimeToUnixTimestamp(DateTimeOffset.UnixEpoch).Should().Be("0");
    }

    [Fact]
    public void HexToRgb_ShouldConvertAKnownColor()
    {
        var result = DevToolsConverters.HexToRgb("#FF0000");

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("rgb(255, 0, 0)").And.Contain("hsl(0, 100%, 50%)");
    }

    [Fact]
    public void HexToRgb_ShouldRejectAnInvalidHexString()
    {
        DevToolsConverters.HexToRgb("not-a-color").Success.Should().BeFalse();
    }
}
