using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class FreeformPositionTests
{
    [Fact]
    public void DefaultFor_ShouldScatterDifferentIndexes_ToDifferentPositions()
    {
        var first = FreeformPosition.DefaultFor(0);
        var second = FreeformPosition.DefaultFor(1);

        (first.X != second.X || first.Y != second.Y).Should().BeTrue("otherwise several widgets switched to Freeform at once would all start stacked exactly on top of each other");
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(150, 65)] // clamped to 100 - Width (35 default)
    public void Clamp_ShouldKeepX_WithinTheCanvas(double x, double expected)
    {
        var position = new FreeformPosition { X = x, Width = 35 };

        position.Clamp();

        position.X.Should().Be(expected);
    }

    [Fact]
    public void Clamp_ShouldEnforceAMinimumSize_ForWidthAndHeight()
    {
        var position = new FreeformPosition { Width = -5, Height = 1 };

        position.Clamp();

        position.Width.Should().Be(5);
        position.Height.Should().Be(5);
    }

    [Fact]
    public void Clamp_ShouldNotShrinkAnOversizedBox_BeyondTheCanvas()
    {
        var position = new FreeformPosition { X = 0, Width = 250 };

        position.Clamp();

        position.Width.Should().Be(100);
        position.X.Should().Be(0);
    }

    [Fact]
    public void ToInlineStyle_ShouldEmitPercentagePositionAndSize_PlusZIndex()
    {
        var position = new FreeformPosition { X = 12.5, Y = 30, Width = 40, Height = 20, Z = 3 };

        position.ToInlineStyle().Should().Be("left:12.5%;top:30%;width:40%;height:20%;z-index:3;");
    }
}
