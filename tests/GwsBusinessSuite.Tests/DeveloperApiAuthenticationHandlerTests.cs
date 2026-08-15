using FluentAssertions;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.Tests;

public sealed class DeveloperApiAuthenticationHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_ShouldBuildAKeyScopedPrincipal()
    {
        var keyId = Guid.NewGuid();
        var fake = new FakeKeyService
        {
            Result = new(keyId, "Reporting", [DeveloperApiScopes.ContactsRead], 75)
        };
        await using var provider = BuildProvider(fake);
        var context = Context(provider, "Bearer gws_live_selector_secret");

        var result = await context.AuthenticateAsync(DeveloperApiAuthenticationDefaults.Scheme);

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.Name.Should().Be("Reporting");
        result.Principal.FindAll(DeveloperApiAuthenticationDefaults.ScopeClaim)
            .Select(claim => claim.Value).Should().ContainSingle(DeveloperApiScopes.ContactsRead);
        result.Principal.FindFirst(DeveloperApiAuthenticationDefaults.RateLimitClaim)!.Value.Should().Be("75");
    }

    [Fact]
    public async Task AuthenticateAsync_ShouldRejectInvalidKeys()
    {
        var fake = new FakeKeyService();
        await using var provider = BuildProvider(fake);

        var result = await Context(provider, "Bearer gws_live_selector_invalid").AuthenticateAsync(DeveloperApiAuthenticationDefaults.Scheme);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_ShouldBoundRepeatedInvalidAttemptsBeforeDatabaseValidation()
    {
        var fake = new FakeKeyService();
        await using var provider = BuildProvider(fake);
        for (var index = 0; index < 35; index++)
        {
            await using var scope = provider.CreateAsyncScope();
            await Context(scope.ServiceProvider, $"Bearer gws_live_selector_invalid{index}").AuthenticateAsync(DeveloperApiAuthenticationDefaults.Scheme);
        }

        fake.AuthenticationAttempts.Should().Be(30);
    }

    private static ServiceProvider BuildProvider(FakeKeyService fake)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IDeveloperApiKeyService>(fake);
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DeveloperApiAuthenticationHandler>(
            DeveloperApiAuthenticationDefaults.Scheme, _ => { });
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Context(IServiceProvider provider, string authorization)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers.Authorization = authorization;
        return context;
    }

    private sealed class FakeKeyService : IDeveloperApiKeyService
    {
        public AuthenticatedDeveloperApiKey? Result { get; set; }
        public int AuthenticationAttempts { get; private set; }
        public Task<AuthenticatedDeveloperApiKey?> AuthenticateAsync(string plaintextKey, CancellationToken cancellationToken = default)
        {
            AuthenticationAttempts++;
            return Task.FromResult(Result);
        }
        public Task<IReadOnlyList<DeveloperApiKeyView>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IssuedDeveloperApiKey> IssueAsync(string name, IReadOnlyCollection<string> scopes, int rateLimitPerMinute, DateTimeOffset? expiresAt, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeAsync(Guid id, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
