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

    [Theory]
    [InlineData(null, "https://example.test/admin/sentinel/window")]
    [InlineData("?page=6f9619ff-8b86-d011-b42d-00cf4fc964ff", "https://example.test/admin/sentinel/window?page=6f9619ff-8b86-d011-b42d-00cf4fc964ff")]
    [InlineData("?database=6f9619ff-8b86-d011-b42d-00cf4fc964ff&row=6f9619ff-8b86-d011-b42d-00cf4fc964fe", "https://example.test/admin/sentinel/window?database=6f9619ff-8b86-d011-b42d-00cf4fc964ff&row=6f9619ff-8b86-d011-b42d-00cf4fc964fe")]
    [InlineData("?page=not-a-guid&next=https://evil.test", "https://example.test/admin/sentinel/window")]
    public void WindowUrl_KeepsOnlyThePageOrDatabaseToOpen(string? query, string expected)
    {
        SentinelWindowNavigation.WindowUrl("https://example.test/", query).Should().Be(expected);
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
