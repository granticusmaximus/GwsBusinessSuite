using System.Reflection;
using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Components.Pages;
using GwsBusinessSuite.Web.Components.Pages.BusinessSuite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace GwsBusinessSuite.Tests;

// Program.cs sets a FallbackPolicy (AdminOnly) so every routed page is protected even
// without an explicit [Authorize] attribute - but that protection is silent and easy to
// lose (e.g. if the fallback policy is ever relaxed or removed). This test requires every
// admin page to declare its own [Authorize] explicitly, so a regression here is a build-time
// test failure instead of a silent access-control gap.
public class PageAuthorizationTests
{
    private static IEnumerable<Type> AdminPageTypes() =>
        typeof(DockerHealth).Assembly.GetTypes()
            .Where(t => t.Namespace == "GwsBusinessSuite.Web.Components.Pages.BusinessSuite")
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any());

    [Fact]
    public void AllAdminPages_ShouldDeclareExplicitAuthorizeAttribute()
    {
        var unprotectedPages = AdminPageTypes()
            .Where(t => !t.GetCustomAttributes<AuthorizeAttribute>().Any())
            .Select(t => t.Name)
            .ToList();

        unprotectedPages.Should().BeEmpty(
            "every page under Components/Pages/BusinessSuite should declare its own " +
            "[Authorize] rather than relying solely on the FallbackPolicy");
    }

    [Fact]
    public void AdminPageTypes_ShouldFindAtLeastTheKnownPageCount()
    {
        // Guards against the discovery query itself silently matching nothing (e.g. after a
        // namespace rename) and making the test above vacuously pass.
        AdminPageTypes().Should().HaveCountGreaterThanOrEqualTo(20);
    }

    [Fact]
    public void Dashboard_ShouldAllowEveryPortalRoleThroughPortalAccessPolicy()
    {
        var authorize = typeof(Home).GetCustomAttribute<AuthorizeAttribute>();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be("PortalAccess");
    }

    [Fact]
    public void PortalRoles_ShouldOnlyContainManagedPortalAccounts()
    {
        AppRoles.All.Should().Equal(AppRoles.Admin, AppRoles.Author, AppRoles.Contributor);
    }
}
