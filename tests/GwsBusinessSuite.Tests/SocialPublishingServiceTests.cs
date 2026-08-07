using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class SocialPublishingServiceTests
{
    // Regression guard for a real finding: once a scheduled post's target failed once (e.g. an
    // expired OAuth token, or a transient network blip), nothing ever retried it and the only
    // signal was a server log line - PublishDueAsync's query only ever selected Status ==
    // Scheduled, and a first failure moved the post straight to the terminal Failed status. This
    // exercises the fix end to end: the first two failures should retry (RetryPending, no
    // alert), and only the third (MaxRetryAttempts) should permanently fail the target and
    // raise a visible alert - including firing SocialPublishingNotifier, the same mechanism
    // NotificationBell subscribes to for a live badge update.
    [Fact]
    public async Task PublishAsync_ShouldRetryTransientFailuresAndOnlyAlertAfterExhaustingRetries()
    {
        await using var db = await CreateDbAsync();
        var alwaysFailingHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}")
        });
        var http = new HttpClient(alwaysFailingHandler);
        var raisedAlerts = new List<SocialPostAlertView>();
        var notifier = new SocialPublishingNotifier();
        notifier.OnAlert += raisedAlerts.Add;
        var service = new SocialPublishingService(
            db, new FakeSecretProtector(), new FakeOllamaService(), http, NullLogger<SocialPublishingService>.Instance, notifier);

        await service.SaveAccountAsync(new SocialAccountInput(
            null, SocialNetworks.X, "Grant on X", "12345", "fake-token", true));
        var account = (await service.GetAccountsAsync()).Single();
        var postId = await service.SaveDraftAsync(
            "Announcing the new release",
            "https://grantwatson.dev/posts/release",
            [new SocialTargetDraft(account.Id, "Check out the new release!")],
            DateTimeOffset.UtcNow);

        // Attempt 1: fails, should retry (not permanently failed, no alert yet).
        var firstResult = await service.PublishAsync(postId);
        firstResult.IsSuccess.Should().BeFalse();
        var afterFirst = (await service.GetPostsAsync()).Single(p => p.Id == postId);
        afterFirst.Status.Should().Be(SocialPostStatuses.Scheduled, "a retry-eligible failure should not surface as a terminal status");
        var targetAfterFirst = afterFirst.Targets.Single();
        targetAfterFirst.Status.Should().Be(SocialPostStatuses.RetryPending);
        targetAfterFirst.RetryCount.Should().Be(1);
        targetAfterFirst.NextRetryAt.Should().NotBeNull();
        raisedAlerts.Should().BeEmpty();
        (await service.CountUnreadAlertsAsync()).Should().Be(0);

        // Simulate the backoff having elapsed (production uses real wall-clock time, which a
        // unit test can't fast-forward) and run two more attempts the same way the scheduler's
        // periodic pass would once NextRetryAt is due.
        await FastForwardRetryAsync(db, targetAfterFirst.Id);
        await service.PublishAsync(postId);
        await FastForwardRetryAsync(db, targetAfterFirst.Id);
        var thirdResult = await service.PublishAsync(postId);

        thirdResult.IsSuccess.Should().BeFalse();
        var afterThird = (await service.GetPostsAsync()).Single(p => p.Id == postId);
        afterThird.Status.Should().Be(SocialPostStatuses.Failed);
        var targetAfterThird = afterThird.Targets.Single();
        targetAfterThird.Status.Should().Be(SocialPostStatuses.Failed);
        targetAfterThird.RetryCount.Should().Be(3);

        raisedAlerts.Should().ContainSingle();
        raisedAlerts[0].PostTitle.Should().Be("Announcing the new release");
        (await service.CountUnreadAlertsAsync()).Should().Be(1);
        (await service.ListAlertsAsync(unreadOnly: true)).Should().ContainSingle(a => a.SocialPostId == postId);
    }

    private static async Task FastForwardRetryAsync(ApplicationDbContext db, Guid targetId)
    {
        var target = await db.SocialPostTargets.FirstAsync(t => t.Id == targetId);
        target.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedValue["protected:".Length..]
            : protectedValue;
    }

    private sealed class FakeOllamaService : IOllamaService
    {
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
