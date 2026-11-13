using System.Numerics;
using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

// Number & Binary Converter (Dev Tools, 12-11-26).
public sealed class DevToolsBaseConverterTests
{
    [Theory]
    [InlineData("255", 10, "11111111", "377", "FF")]
    [InlineData("0xff", 16, "11111111", "377", "FF")]
    [InlineData("1111_1111", 2, "11111111", "377", "FF")]
    [InlineData("0b1010", 2, "1010", "12", "A")]
    [InlineData("-42", 10, "-101010", "-52", "-2A")]
    [InlineData("0", 8, "0", "0", "0")]
    public void Views_ShouldShowEveryBase(string input, int numberBase, string binary, string octal, string hex)
    {
        DevToolsBaseConverter.TryParse(input, numberBase, out var value, out var error).Should().BeTrue(error);
        var views = DevToolsBaseConverter.Views(value, 36);
        views.Binary.Should().Be(binary);
        views.Octal.Should().Be(octal);
        views.Hex.Should().Be(hex);
    }

    [Fact]
    public void Parse_ShouldHaveNo64BitCeiling_AndRoundTripAnyBase()
    {
        var huge = BigInteger.Pow(2, 200) + 12345;
        for (var numberBase = 2; numberBase <= 36; numberBase++)
        {
            DevToolsBaseConverter.TryParse(DevToolsBaseConverter.ToBase(huge, numberBase), numberBase, out var back, out _).Should().BeTrue();
            back.Should().Be(huge, $"base {numberBase} round-trips");
        }
        DevToolsBaseConverter.Views(huge, 36).BitLength.Should().Be(201);
    }

    [Theory]
    [InlineData("102", 2, "'2' isn't a base-2 digit (use 0-1).")]
    [InlineData("0x1F", 10, "'0x' means base 16, but the input base is 10.")]
    [InlineData("g", 16, "'g' isn't a base-16 digit (use 0-9 and a-f).")]
    [InlineData("  ", 10, "Enter a number.")]
    public void Parse_ShouldExplainWhatsWrong(string input, int numberBase, string expected)
    {
        DevToolsBaseConverter.TryParse(input, numberBase, out _, out var error).Should().BeFalse();
        error.Should().Be(expected);
    }

    [Fact]
    public void Parse_ShouldReadAHexNumberStartingWith0b_AsHex()
    {
        // In hex, "0b1" is the number 0xB1, not a binary prefix.
        DevToolsBaseConverter.TryParse("0b1", 16, out var value, out _).Should().BeTrue();
        value.Should().Be(new BigInteger(0xB1));
    }

    [Fact]
    public void Group_ShouldSplitFromTheRight()
    {
        DevToolsBaseConverter.Group("101010", 4).Should().Be("10 1010");
        DevToolsBaseConverter.Group("1234567", 3, ',').Should().Be("1,234,567");
        DevToolsBaseConverter.Group("-1234567", 3, ',').Should().Be("-1,234,567");
    }

    [Fact]
    public void TwosComplement_ShouldShowFixedWidthRegisters()
    {
        var minusOne = DevToolsBaseConverter.TwosComplement(-1, 8)!;
        minusOne.Binary.Should().Be("11111111");
        minusOne.Hex.Should().Be("FF");
        minusOne.Unsigned.Should().Be("255");
        minusOne.Signed.Should().Be("-1");

        var big = DevToolsBaseConverter.TwosComplement(200, 8)!;
        big.Signed.Should().Be("-56", "200 in a byte reads as -56 signed");
        DevToolsBaseConverter.TwosComplement(-129, 8).Should().BeNull();
        DevToolsBaseConverter.TwosComplement(256, 8).Should().BeNull();
        DevToolsBaseConverter.TwosComplement(5, 16)!.Binary.Should().Be("0000000000000101");
    }

    [Fact]
    public void Text_ShouldRoundTripThroughEveryByteBase_IncludingEmoji()
    {
        DevToolsBaseConverter.TextToBytes("Hi", 2).Should().Be("01001000 01101001");
        DevToolsBaseConverter.TextToBytes("Hi", 16).Should().Be("48 69");
        DevToolsBaseConverter.TextToBytes("Hi", 10).Should().Be("72 105");
        DevToolsBaseConverter.TextToBytes("Hi", 8).Should().Be("110 151");
        foreach (var numberBase in new[] { 2, 8, 10, 16 })
        {
            var bytes = DevToolsBaseConverter.TextToBytes("Café ☕ 🚀", numberBase);
            DevToolsBaseConverter.TryBytesToText(bytes, numberBase, out var text, out var error).Should().BeTrue(error);
            text.Should().Be("Café ☕ 🚀");
        }
    }

    [Fact]
    public void BytesToText_ShouldAcceptUnseparatedBinaryAndHex_AndExplainBadInput()
    {
        DevToolsBaseConverter.TryBytesToText("0100100001101001", 2, out var fromBinary, out _).Should().BeTrue();
        fromBinary.Should().Be("Hi");
        DevToolsBaseConverter.TryBytesToText("4869", 16, out var fromHex, out _).Should().BeTrue();
        fromHex.Should().Be("Hi");
        DevToolsBaseConverter.TryBytesToText("0x48, 0x69", 16, out var prefixed, out _).Should().BeTrue();
        prefixed.Should().Be("Hi");

        DevToolsBaseConverter.TryBytesToText("010010000", 2, out _, out var split).Should().BeFalse();
        split.Should().Contain("whole 8-digit bytes");
        DevToolsBaseConverter.TryBytesToText("300", 10, out _, out var range).Should().BeFalse();
        range.Should().Contain("outside 0-255");
        DevToolsBaseConverter.TryBytesToText("ff", 16, out _, out var utf8).Should().BeFalse();
        utf8.Should().Be("Those bytes aren't valid UTF-8 text.");
    }

    [Theory]
    [InlineData(12, "and", 10, 8)]
    [InlineData(12, "or", 10, 14)]
    [InlineData(12, "xor", 10, 6)]
    [InlineData(1, "shl", 10, 1024)]
    [InlineData(1024, "shr", 3, 128)]
    [InlineData(7, "/", 2, 3)]
    [InlineData(7, "%", 2, 1)]
    [InlineData(2, "pow", 10, 1024)]
    [InlineData(5, "not", 0, -6)]
    [InlineData(12, "nand", 10, -9)]
    public void Calculate_ShouldDoArithmeticAndBitwiseOperations(long a, string op, long b, long expected)
    {
        DevToolsBaseConverter.TryCalculate(a, op, b, out var result, out var error).Should().BeTrue(error);
        result.Should().Be(new BigInteger(expected));
    }

    [Fact]
    public void Calculate_ShouldRefuseDivisionByZeroAndHugeShifts()
    {
        DevToolsBaseConverter.TryCalculate(1, "/", 0, out _, out var error).Should().BeFalse();
        error.Should().Be("Can't divide by zero.");
        DevToolsBaseConverter.TryCalculate(1, "shl", 1_000_000, out _, out _).Should().BeFalse();
    }
}
