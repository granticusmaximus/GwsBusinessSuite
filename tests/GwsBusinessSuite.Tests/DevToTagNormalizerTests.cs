using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class DevToTagNormalizerTests
{
    [Theory]
    [InlineData("C#", "csharp")]
    [InlineData("c#", "csharp")]
    [InlineData("csharp", "csharp")]
    [InlineData(".NET", "dotnet")]
    [InlineData(".net", "dotnet")]
    [InlineData("dotnet", "dotnet")]
    [InlineData("F#", "fsharp")]
    [InlineData("Node.js", "node")]
    [InlineData("nodejs", "node")]
    [InlineData("C++", "cpp")]
    public void Normalize_ShouldMapKnownAliases_ToTheirRealDevToTag(string keyword, string expectedTag)
    {
        Assert.Equal(expectedTag, DevToTagNormalizer.Normalize(keyword));
    }

    [Theory]
    [InlineData("Blazor", "blazor")]
    [InlineData("Python", "python")]
    [InlineData("  python  ", "python")]
    public void Normalize_ShouldLowercaseAndStripPunctuation_ForUnaliasedKeywords(string keyword, string expectedTag)
    {
        Assert.Equal(expectedTag, DevToTagNormalizer.Normalize(keyword));
    }
}
