using System.Security.Claims;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Web.Components.Layout;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelWindowLayoutTests
{
    [Fact]
    public async Task WindowRoute_RendersTheWorkspaceWithoutAdminNavigation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<AdminThemeProvider>();
        services.AddSingleton<IUserPreferenceService, TestPreferences>();
        services.AddSingleton<ICurrentUserAccessor>(new FixedCurrentUserAccessor("test"));
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMemoryCache>().Set("admin-theme-css", string.Empty);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TestLayoutHost>()).ToHtmlString());

        html.Should().Contain("id=\"sentinel-test-workspace\"");
        html.Should().Contain("gws-builder-mode");
        html.Should().NotContain("gws-admin-bar");
        html.Should().NotContain("gws-sidebar");
        html.Should().NotContain("gws-bottom-bar");
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://example.test/", "https://example.test/admin/sentinel/window?page=123");
    }

    private sealed class TestPreferences : IUserPreferenceService
    {
        public Task<string> GetAsync(string username, string key, CancellationToken ct = default) =>
            Task.FromResult(key == UserPreferenceKeys.Density ? Densities.Comfortable : DensityOffers.Answered);

        public Task SetAsync(string username, string key, string value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestLayoutHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"));
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, "Value", Task.FromResult(new AuthenticationState(user)));
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<MainLayout>(0);
                child.AddAttribute(1, "Body", (RenderFragment)(body =>
                    body.AddMarkupContent(0, "<div id=\"sentinel-test-workspace\"></div>")));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
