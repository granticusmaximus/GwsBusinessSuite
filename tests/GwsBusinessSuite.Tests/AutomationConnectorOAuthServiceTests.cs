using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class AutomationConnectorOAuthServiceTests
{
    [Fact]
    public async Task SlackOAuthService_CompleteAuthorizationAsync_ShouldSaveAnOAuth2CredentialOnSuccess()
    {
        await using var fixture = await Fixture.CreateAsync(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be("https://slack.com/api/oauth.v2.access");
            return JsonResponse("""{"ok":true,"access_token":"xoxb-slack-token","team":{"name":"Acme Co"}}""");
        });
        var service = new SlackOAuthService(
            fixture.HttpClient,
            Options.Create(new SlackOAuthOptions { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/auth/slack/callback" }),
            fixture.CredentialService,
            NullLogger<SlackOAuthService>.Instance);

        var result = await service.CompleteAuthorizationAsync("auth-code");

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("Acme Co");
        var credential = await fixture.Db.AutomationCredentials.SingleAsync();
        credential.TypeKey.Should().Be(AutomationCredentialService.OAuth2TypeKey);
        var decrypted = await fixture.CredentialService.GetDecryptedDataAsync(credential.Id);
        decrypted.Should().Contain("xoxb-slack-token").And.Contain("tokenEndpoint");
    }

    [Fact]
    public async Task SlackOAuthService_CompleteAuthorizationAsync_ShouldFail_WhenSlackReportsAnError()
    {
        await using var fixture = await Fixture.CreateAsync(_ => JsonResponse("""{"ok":false,"error":"invalid_code"}"""));
        var service = new SlackOAuthService(
            fixture.HttpClient,
            Options.Create(new SlackOAuthOptions { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/auth/slack/callback" }),
            fixture.CredentialService,
            NullLogger<SlackOAuthService>.Instance);

        var result = await service.CompleteAuthorizationAsync("bad-code");

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("invalid_code");
        (await fixture.Db.AutomationCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SlackOAuthService_ShouldRefuseToStartOrCompleteAuthorization_WhenNotConfigured()
    {
        await using var fixture = await Fixture.CreateAsync(_ => throw new InvalidOperationException("HTTP should not be called when unconfigured."));
        var service = new SlackOAuthService(
            fixture.HttpClient, Options.Create(new SlackOAuthOptions()), fixture.CredentialService, NullLogger<SlackOAuthService>.Instance);

        service.IsConfigured.Should().BeFalse();
        var act = () => service.CreateAuthorizationUrl("state");
        act.Should().Throw<InvalidOperationException>();
        (await service.CompleteAuthorizationAsync("code")).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GoogleOAuthService_CompleteAuthorizationAsync_ShouldSaveAnOAuth2CredentialWithExpiry()
    {
        await using var fixture = await Fixture.CreateAsync(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be("https://oauth2.googleapis.com/token");
            return JsonResponse("""{"access_token":"ya29.google-token","refresh_token":"1//refresh","expires_in":3600}""");
        });
        var service = new GoogleOAuthService(
            fixture.HttpClient,
            Options.Create(new GoogleOAuthOptions { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/auth/google/callback" }),
            fixture.CredentialService,
            NullLogger<GoogleOAuthService>.Instance);

        var result = await service.CompleteAuthorizationAsync("auth-code");

        result.IsSuccess.Should().BeTrue();
        var credential = await fixture.Db.AutomationCredentials.SingleAsync();
        var decrypted = await fixture.CredentialService.GetDecryptedDataAsync(credential.Id);
        decrypted.Should().Contain("ya29.google-token").And.Contain("expiresAt").And.Contain("1//refresh");
    }

    [Fact]
    public async Task GoogleOAuthService_CompleteAuthorizationAsync_ShouldFail_OnNonSuccessStatus()
    {
        await using var fixture = await Fixture.CreateAsync(_ => JsonResponse("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        var service = new GoogleOAuthService(
            fixture.HttpClient,
            Options.Create(new GoogleOAuthOptions { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/auth/google/callback" }),
            fixture.CredentialService,
            NullLogger<GoogleOAuthService>.Instance);

        (await service.CompleteAuthorizationAsync("bad-code")).IsSuccess.Should().BeFalse();
        (await fixture.Db.AutomationCredentials.CountAsync()).Should().Be(0);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, HttpClient httpClient, AutomationCredentialService credentialService)
        {
            _connection = connection;
            Db = db;
            HttpClient = httpClient;
            CredentialService = credentialService;
        }

        public ApplicationDbContext Db { get; }
        public HttpClient HttpClient { get; }
        public AutomationCredentialService CredentialService { get; }

        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var httpClient = new HttpClient(new StubHandler(responder));
            var credentialService = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
            return new Fixture(connection, db, httpClient, credentialService);
        }

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext))}";
        public string Unprotect(string protectedText) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedText["protected::".Length..]));
    }
}
