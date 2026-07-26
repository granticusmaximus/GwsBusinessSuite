using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class NotionOAuthServiceTests
{
    [Fact]
    public async Task CompleteAuthorizationAsync_ShouldExchangeAndEncryptOAuthTokens()
    {
        await using var fixture = await OAuthFixture.CreateAsync(request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.PathAndQuery.Should().Be("/v1/oauth/token");
            request.Headers.Authorization.Should().BeEquivalentTo(
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("client-id:client-secret"))));
            request.Headers.GetValues("Notion-Version").Should().ContainSingle()
                .Which.Should().Be(NotionService.NotionVersion);
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                .Should().Contain("\"grant_type\":\"authorization_code\"")
                .And.Contain("\"code\":\"authorization-code\"")
                .And.Contain("\"redirect_uri\":\"https://example.test/auth/notion/callback\"");
            return JsonResponse(
                """
                {
                  "access_token":"access-token",
                  "refresh_token":"refresh-token",
                  "bot_id":"11111111-1111-1111-1111-111111111111",
                  "workspace_id":"22222222-2222-2222-2222-222222222222",
                  "workspace_name":"Grant Workspace",
                  "workspace_icon":"https://example.test/icon.png"
                }
                """);
        });

        var result = await fixture.Service.CompleteAuthorizationAsync("authorization-code");

        result.IsSuccess.Should().BeTrue();
        result.WorkspaceName.Should().Be("Grant Workspace");
        var settings = await fixture.Db.NotionConnectorSettings.SingleAsync();
        settings.IntegrationToken.Should().Be("protected::access-token");
        settings.OAuthRefreshToken.Should().Be("protected::refresh-token");
        settings.AuthenticationMode.Should().Be("oauth");
        settings.OAuthBotId.Should().Be("11111111-1111-1111-1111-111111111111");
        settings.WorkspaceId.Should().Be("22222222-2222-2222-2222-222222222222");
        settings.WorkspaceName.Should().Be("Grant Workspace");
        settings.WorkspaceIconUrl.Should().Be("https://example.test/icon.png");
        settings.OAuthConnectedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshAsync_ShouldRotateBothTokens()
    {
        await using var fixture = await OAuthFixture.CreateAsync(request =>
        {
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                .Should().Contain("\"grant_type\":\"refresh_token\"")
                .And.Contain("\"refresh_token\":\"old-refresh\"");
            return JsonResponse(
                """
                {
                  "access_token":"new-access",
                  "refresh_token":"new-refresh",
                  "bot_id":"11111111-1111-1111-1111-111111111111",
                  "workspace_id":"22222222-2222-2222-2222-222222222222",
                  "workspace_name":"Grant Workspace",
                  "workspace_icon":null
                }
                """);
        });
        fixture.Db.NotionConnectorSettings.Add(new NotionConnectorSettings
        {
            Id = NotionConnectorSettings.WellKnownId,
            IntegrationToken = "protected::old-access",
            OAuthRefreshToken = "protected::old-refresh",
            AuthenticationMode = "oauth",
            OAuthBotId = "11111111-1111-1111-1111-111111111111",
            WorkspaceId = "22222222-2222-2222-2222-222222222222"
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.RefreshAsync();

        result.IsSuccess.Should().BeTrue();
        var settings = await fixture.Db.NotionConnectorSettings.SingleAsync();
        settings.IntegrationToken.Should().Be("protected::new-access");
        settings.OAuthRefreshToken.Should().Be("protected::new-refresh");
    }

    [Fact]
    public async Task DisconnectAsync_ShouldRevokeBeforeClearingStoredTokens()
    {
        await using var fixture = await OAuthFixture.CreateAsync(request =>
        {
            request.RequestUri!.PathAndQuery.Should().Be("/v1/oauth/revoke");
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                .Should().Contain("\"token\":\"access-token\"");
            return JsonResponse("""{"request_id":"33333333-3333-3333-3333-333333333333"}""");
        });
        fixture.Db.NotionConnectorSettings.Add(new NotionConnectorSettings
        {
            Id = NotionConnectorSettings.WellKnownId,
            IntegrationToken = "protected::access-token",
            OAuthRefreshToken = "protected::refresh-token",
            AuthenticationMode = "oauth",
            OAuthBotId = "11111111-1111-1111-1111-111111111111",
            WorkspaceId = "22222222-2222-2222-2222-222222222222"
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.DisconnectAsync();

        result.IsSuccess.Should().BeTrue();
        var settings = await fixture.Db.NotionConnectorSettings.SingleAsync();
        settings.IntegrationToken.Should().BeEmpty();
        settings.OAuthRefreshToken.Should().BeEmpty();
        settings.AuthenticationMode.Should().Be("internal");
        settings.OAuthBotId.Should().BeNull();
        settings.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public async Task CreateAuthorizationUrl_ShouldIncludeRedirectAndOpaqueState()
    {
        await using var fixture = await OAuthFixture.CreateAsync(_ =>
            throw new InvalidOperationException("No HTTP request expected."));

        var url = fixture.Service.CreateAuthorizationUrl("protected-state");

        url.Should().StartWith("https://api.notion.com/v1/oauth/authorize?");
        url.Should().Contain("owner=user");
        url.Should().Contain("client_id=client-id");
        url.Should().Contain("redirect_uri=https%3A%2F%2Fexample.test%2Fauth%2Fnotion%2Fcallback");
        url.Should().Contain("response_type=code");
        url.Should().Contain("state=protected-state");
        url.Should().NotContain("client-secret");
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class OAuthFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private OAuthFixture(
            SqliteConnection connection,
            ApplicationDbContext db,
            NotionOAuthService service)
        {
            _connection = connection;
            Db = db;
            Service = service;
        }

        public ApplicationDbContext Db { get; }
        public NotionOAuthService Service { get; }

        public static async Task<OAuthFixture> CreateAsync(
            Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            var httpClient = new HttpClient(new StubHandler(responder))
            {
                BaseAddress = new Uri("https://api.notion.com/v1/")
            };
            var options = Options.Create(new NotionOAuthOptions
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
                RedirectUri = "https://example.test/auth/notion/callback"
            });
            var service = new NotionOAuthService(
                httpClient,
                options,
                db,
                new FakeSecretProtector(),
                NullLogger<NotionOAuthService>.Instance);
            return new OAuthFixture(connection, db, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("protected::", StringComparison.Ordinal)
                ? protectedValue["protected::".Length..]
                : throw new InvalidOperationException("Unreadable protected value.");
    }
}
