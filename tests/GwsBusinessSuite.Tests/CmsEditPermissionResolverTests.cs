using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsEditPermissionResolverTests
{
    [Theory]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.Inherit, CmsEditPermissions.Inherit, CmsEditPermissions.Open)]
    [InlineData(CmsEditPermissions.Locked, CmsEditPermissions.Inherit, CmsEditPermissions.Inherit, CmsEditPermissions.Locked)]
    [InlineData(CmsEditPermissions.Locked, CmsEditPermissions.Open, CmsEditPermissions.Inherit, CmsEditPermissions.Open)]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.Locked, CmsEditPermissions.Inherit, CmsEditPermissions.Locked)]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.Locked, CmsEditPermissions.Open, CmsEditPermissions.Open)]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.Open, CmsEditPermissions.Locked, CmsEditPermissions.Locked)]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.Inherit, CmsEditPermissions.ContentOnly, CmsEditPermissions.ContentOnly)]
    public void ResolveForWidget_ShouldPreferWidget_ThenSection_ThenPage(
        string pageDefault, string sectionValue, string widgetValue, string expected)
    {
        CmsEditPermissionResolver.ResolveForWidget(pageDefault, sectionValue, widgetValue).Should().Be(expected);
    }

    [Fact]
    public void ResolveForWidget_ShouldTreatBlankPageDefault_AsOpen()
    {
        CmsEditPermissionResolver.ResolveForWidget(string.Empty, CmsEditPermissions.Inherit, CmsEditPermissions.Inherit)
            .Should().Be(CmsEditPermissions.Open);
    }

    [Theory]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.Inherit, CmsEditPermissions.Open)]
    [InlineData(CmsEditPermissions.Locked, CmsEditPermissions.Inherit, CmsEditPermissions.Locked)]
    [InlineData(CmsEditPermissions.Locked, CmsEditPermissions.Open, CmsEditPermissions.Open)]
    [InlineData(CmsEditPermissions.Open, CmsEditPermissions.ContentOnly, CmsEditPermissions.ContentOnly)]
    public void ResolveForSection_ShouldPreferSection_ThenPage(string pageDefault, string sectionValue, string expected)
    {
        CmsEditPermissionResolver.ResolveForSection(pageDefault, sectionValue).Should().Be(expected);
    }
}
