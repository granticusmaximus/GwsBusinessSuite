using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

public sealed class DevToolsConvertersTests
{
    [Theory]
    [InlineData("255", 10, 16, "FF")]
    [InlineData("FF", 16, 10, "255")]
    [InlineData("1010", 2, 10, "10")]
    [InlineData("10", 10, 2, "1010")]
    [InlineData("17", 10, 8, "21")]
    public void ConvertNumberBase_ShouldConvertBetweenBases(string input, int fromBase, int toBase, string expected)
    {
        var result = DevToolsConverters.ConvertNumberBase(input, fromBase, toBase);

        result.Success.Should().BeTrue();
        result.Output.Should().Be(expected);
    }

    [Fact]
    public void ConvertNumberBase_ShouldFailCleanlyForAnInvalidDigitInTheSourceBase()
    {
        DevToolsConverters.ConvertNumberBase("XYZ", 16, 10).Success.Should().BeFalse();
    }

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
