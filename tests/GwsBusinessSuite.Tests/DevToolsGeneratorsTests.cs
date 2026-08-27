using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

public sealed class DevToolsGeneratorsTests
{
    // Known test vectors for the empty string, so a regression in the hex-casing/algorithm
    // choice would be caught rather than just asserting "looks like a hash."
    [Fact]
    public void Hashes_ShouldMatchKnownVectorsForTheEmptyString()
    {
        DevToolsGenerators.HashMd5("").Should().Be("d41d8cd98f00b204e9800998ecf8427e");
        DevToolsGenerators.HashSha1("").Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
        DevToolsGenerators.HashSha256("").Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        DevToolsGenerators.HashSha512("").Should().Be(
            "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e");
    }

    [Fact]
    public void NewGuid_ShouldRespectCaseAndHyphenOptions()
    {
        DevToolsGenerators.NewGuid(uppercase: false, includeHyphens: true).Should().MatchRegex("^[0-9a-f-]{36}$");
        DevToolsGenerators.NewGuid(uppercase: true, includeHyphens: true).Should().MatchRegex("^[0-9A-F-]{36}$");
        DevToolsGenerators.NewGuid(uppercase: false, includeHyphens: false).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void GeneratePassword_ShouldProduceTheRequestedLengthFromOnlyTheRequestedCharacterSets()
    {
        var result = DevToolsGenerators.GeneratePassword(20, includeUpper: false, includeDigits: false, includeSymbols: false);

        result.Success.Should().BeTrue();
        result.Output.Should().HaveLength(20);
        result.Output.Should().MatchRegex("^[a-z]{20}$");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(300)]
    public void GeneratePassword_ShouldRejectLengthsOutsideTheAllowedRange(int length)
    {
        DevToolsGenerators.GeneratePassword(length, true, true, true).Success.Should().BeFalse();
    }

    [Fact]
    public void GenerateLoremIpsum_ShouldProduceTheRequestedParagraphCount()
    {
        var text = DevToolsGenerators.GenerateLoremIpsum(paragraphs: 3, sentencesPerParagraph: 2);

        text.Split("\n\n").Should().HaveCount(3);
    }
}
