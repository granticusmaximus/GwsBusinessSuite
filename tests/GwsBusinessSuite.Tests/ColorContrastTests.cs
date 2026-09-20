using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class ColorContrastTests
{
    [Theory]
    [InlineData("#0f172a")]
    [InlineData("#1c3d5a")]
    [InlineData("#000000")]
    public void GetReadableTextColor_ShouldReturnLightText_ForADarkBackground(string backgroundHex)
    {
        ColorContrast.GetReadableTextColor(backgroundHex).Should().Be("#f8fafc");
    }

    [Theory]
    [InlineData("#f8fafc")]
    [InlineData("#ffffff")]
    [InlineData("#e2e8f0")]
    public void GetReadableTextColor_ShouldReturnDarkText_ForALightBackground(string backgroundHex)
    {
        ColorContrast.GetReadableTextColor(backgroundHex).Should().Be("#0f172a");
    }

    [Fact]
    public void GetReadableTextColor_ShouldSupportThreeDigitShorthandHex()
    {
        ColorContrast.GetReadableTextColor("#fff").Should().Be("#0f172a");
        ColorContrast.GetReadableTextColor("#000").Should().Be("#f8fafc");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-color")]
    [InlineData("#12")]
    public void GetReadableTextColor_ShouldFallBackToDarkText_ForAnUnparseableValue(string backgroundHex)
    {
        ColorContrast.GetReadableTextColor(backgroundHex).Should().Be("#0f172a");
    }
}
