using System.Security.Cryptography;
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

namespace GwsBusinessSuite.Tests;

public sealed class NotionWebhookServiceTests
{
    [Fact]
    public async Task VerificationPayload_ShouldProtectAndPersistSigningToken()
    {
        await using var fixture = await WebhookFixture.CreateAsync();

        var result = await fixture.Service.HandleAsync(
            """{"verification_token":"secret-webhook-token"}""",
            null);

        result.StatusCode.Should().Be(200);
        var settings = await fixture.Db.NotionConnectorSettings.SingleAsync();
        settings.WebhookVerificationToken.Should().Be("protected:secret-webhook-token");
        settings.WebhookVerificationReceivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SignedEvent_ShouldQueueOneRefreshAndDeduplicateRetries()
    {
        await using var fixture = await WebhookFixture.CreateAsync();
        await fixture.Service.HandleAsync(
            """{"verification_token":"secret-webhook-token"}""",
            null);
        var payload =
            """{"id":"event-1","timestamp":"2026-07-27T19:40:00Z","workspace_id":"workspace-1","type":"page.content_updated","entity":{"id":"page-1","type":"page"}}""";
        var signature = Sign(payload, "secret-webhook-token");

        var first = await fixture.Service.HandleAsync(payload, signature);
        var retry = await fixture.Service.HandleAsync(payload, signature);

        first.StatusCode.Should().Be(200);
        retry.StatusCode.Should().Be(200);
        fixture.Coordinator.WebhookQueueCount.Should().Be(1);
        (await fixture.Db.NotionWebhookEvents.SingleAsync()).Should().BeEquivalentTo(
            new
            {
                NotionEventId = "event-1",
                EventType = "page.content_updated",
                WorkspaceId = "workspace-1",
                EntityId = "page-1",
                EntityType = "page",
                SyncQueued = true
            });
    }

    [Fact]
    public async Task EventWithInvalidSignature_ShouldBeRejectedWithoutPersisting()
    {
        await using var fixture = await WebhookFixture.CreateAsync();
        await fixture.Service.HandleAsync(
            """{"verification_token":"secret-webhook-token"}""",
            null);

        var result = await fixture.Service.HandleAsync(
            """{"id":"event-1","type":"page.content_updated"}""",
            "sha256=incorrect");

        result.StatusCode.Should().Be(401);
        (await fixture.Db.NotionWebhookEvents.CountAsync()).Should().Be(0);
        fixture.Coordinator.WebhookQueueCount.Should().Be(0);
    }

    [Fact]
    public async Task SecondVerificationPayload_ShouldNotReplaceEstablishedSigningToken()
    {
        await using var fixture = await WebhookFixture.CreateAsync();
        await fixture.Service.HandleAsync(
            """{"verification_token":"original-token"}""",
            null);

        var result = await fixture.Service.HandleAsync(
            """{"verification_token":"attacker-token"}""",
            null);

        result.StatusCode.Should().Be(409);
        (await fixture.Db.NotionConnectorSettings.SingleAsync())
            .WebhookVerificationToken.Should().Be("protected:original-token");
    }

    private static string Sign(string payload, string secret)
    {
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private sealed class WebhookFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private WebhookFixture(
            SqliteConnection connection,
            ApplicationDbContext db,
            FakeCoordinator coordinator)
        {
            _connection = connection;
            Db = db;
            Coordinator = coordinator;
            Service = new NotionWebhookService(
                db,
                new PrefixSecretProtector(),
                coordinator,
                NullLogger<NotionWebhookService>.Instance);
        }

        public ApplicationDbContext Db { get; }
        public FakeCoordinator Coordinator { get; }
        public NotionWebhookService Service { get; }

        public static async Task<WebhookFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            db.NotionConnectorSettings.Add(new NotionConnectorSettings
            {
                Id = NotionConnectorSettings.WellKnownId,
                IntegrationToken = "protected:notion-token"
            });
            await db.SaveChangesAsync();
            return new WebhookFixture(connection, db, new FakeCoordinator());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class PrefixSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : throw new CryptographicException();
    }

    private sealed class FakeCoordinator : INotionSyncCoordinator
    {
        public int WebhookQueueCount { get; private set; }
        public bool TryQueueManualSync() => true;
        public bool TryQueueWebhookSync()
        {
            WebhookQueueCount++;
            return true;
        }

        public NotionSyncJobStatus GetStatus() => NotionSyncJobStatus.Idle;
    }
}
