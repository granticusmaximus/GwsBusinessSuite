using System.Reflection;
using FluentAssertions;
using GwsBusinessSuite.App;
using GwsBusinessSuite.Web.Components.Pages.BusinessSuite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelWindowTests
{
    [Theory]
    [InlineData("/admin", (int)SentinelWindowDestination.Workspace)]
    [InlineData("/admin/sentinel?page=123", (int)SentinelWindowDestination.Workspace)]
    [InlineData("/admin/wiki?database=123", (int)SentinelWindowDestination.Workspace)]
    [InlineData("/admin/sentinel/window?page=123", (int)SentinelWindowDestination.Allow)]
    [InlineData("/admin/sentinel/present/123", (int)SentinelWindowDestination.Allow)]
    [InlineData("/admin/login/mfa", (int)SentinelWindowDestination.Allow)]
    [InlineData("/auth/touchid/web-login", (int)SentinelWindowDestination.Allow)]
    [InlineData("/admin/osint", (int)SentinelWindowDestination.External)]
    [InlineData("/admin/sentinel/window-other", (int)SentinelWindowDestination.External)]
    public void Navigation_StaysInSentinel_AndAllowsSignIn(string path, int expected)
    {
        SentinelWindowNavigation.Classify(new Uri("https://example.test" + path))
            .Should().Be((SentinelWindowDestination)expected);
    }

    [Fact]
    public void WindowRoute_UsesTheExistingAuthorizedSentinelEditor()
    {
        typeof(Wiki).GetCustomAttributes<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .Should().Contain("/admin/sentinel/window");
        typeof(Wiki).GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be("ContentAccess");
    }
}
