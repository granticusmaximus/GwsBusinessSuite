using FluentAssertions;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

public class PortalNavigationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/admin")]
    [InlineData("//example.com/admin")]
    [InlineData("/\\example.com/admin")]
    public void ResolvePostLoginPath_ShouldDefaultToDashboard_WhenReturnPathIsMissingOrUnsafe(string? returnUrl)
    {
        PortalNavigation.ResolvePostLoginPath(returnUrl).Should().Be("/admin");
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/content-studio")]
    [InlineData("/admin/article-editor?status=draft")]
    public void ResolvePostLoginPath_ShouldPreserveSafePortalReturnPath(string returnUrl)
    {
        PortalNavigation.ResolvePostLoginPath(returnUrl).Should().Be(returnUrl);
    }
}
