using FluentAssertions;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace GwsBusinessSuite.Tests;

public sealed class CurrentUserAccessorTests
{
    [Fact]
    public async Task GetCurrentUsernameAsync_ShouldReturnUnknown_RatherThanThrow_WhenCalledOutsideAnActiveCircuit()
    {
        // ServerAuthenticationStateProvider.GetAuthenticationStateAsync throws
        // InvalidOperationException when there's no active Razor Components circuit to read -
        // a real, currently-live failure mode for any startup-time or background caller (e.g.
        // Program.cs's seed steps, which run before any user ever connects and therefore have
        // no HttpContext either). Regression guard for the boot-time crash this caused in
        // AutomationWorkflowService.PublishAsync -> SecurityAuditService.RecordAsync.
        var accessor = new CurrentUserAccessor(
            new NoHttpContextAccessor(),
            new ThrowingAuthenticationStateProvider());

        var username = await accessor.GetCurrentUsernameAsync();

        username.Should().Be("unknown");
    }

    [Fact]
    public async Task GetCurrentUsernameAsync_ShouldPreferTheHttpContextUsername_WhenOneIsAvailable()
    {
        var accessor = new CurrentUserAccessor(
            new FakeHttpContextAccessor("grant"),
            new ThrowingAuthenticationStateProvider());

        var username = await accessor.GetCurrentUsernameAsync();

        username.Should().Be("grant");
    }

    [Fact]
    public async Task GetCurrentUsernameAsync_ShouldFallBackToTheCircuitUsername_WhenThereIsNoHttpContextUser()
    {
        var accessor = new CurrentUserAccessor(
            new NoHttpContextAccessor(),
            new FakeAuthenticationStateProvider("grant"));

        var username = await accessor.GetCurrentUsernameAsync();

        username.Should().Be("grant");
    }

    private sealed class NoHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeHttpContextAccessor(string username) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = BuildAuthenticatedContext(username);

        private static DefaultHttpContext BuildAuthenticatedContext(string username)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test");
            return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }
    }

    private sealed class ThrowingAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException(
                "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component.");
    }

    private sealed class FakeAuthenticationStateProvider(string username) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
