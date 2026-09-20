using FluentAssertions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Application.AppGeneration;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Regression guard for a real gap: this drives the summary card every admin session opens
// with (pending drafts/comments/follow-ups/alerts/approvals), and had zero test coverage.
// Investigating it surfaced a real bug beyond just "untested": GetAsync memoized its result
// in a bare `_summaryTask ??= ...` field with no key on includeAdminMetrics - fine for a
// single page load, but this service is Scoped and Blazor Server's DI scope is per-circuit,
// so the first call's result (admin or non-admin) would silently keep being returned for the
// rest of that user's session. Fixed to a short per-key TTL cache; these tests pin both the
// aggregation and the fix.
public sealed class AdminPortalSummaryServiceTests
{
    [Fact]
    public async Task GetAsync_ShouldAggregateCountsAcrossAllSixSources()
    {
        await using var db = await CreateDbAsync();
        var article = new Article { Slug = "test-article", Title = "Test Article" };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        db.Comments.Add(new Comment { ArticleId = article.Id, AuthorName = "Reader", Status = CommentStatuses.Pending });
        db.Comments.Add(new Comment { ArticleId = article.Id, AuthorName = "Spammer", Status = CommentStatuses.Spam });
        var contact = new Contact { FullName = "Jordan Lee", FollowUpDate = DateTimeOffset.UtcNow.AddDays(-1) };
        db.Contacts.Add(contact);
        db.DockerHealthAlerts.Add(new DockerHealthAlert { ContainerName = "gwssuite-web", Message = "Restarted.", IsRead = false });
        await db.SaveChangesAsync();

        var contentStudio = new FakeContentStudioService(pendingReview: 3);
        var appGeneration = new FakeAppGenerationService(pendingApproval: 2);
        var formSubmissions = new FakeFormSubmissionService(unread: 4);
        var service = new AdminPortalSummaryService(
            new CommentService(db), contentStudio, new CrmService(db), new DockerHealthService(db), appGeneration, formSubmissions);

        var summary = await service.GetAsync(includeAdminMetrics: true);

        summary.PendingDrafts.Should().Be(3);
        summary.PendingComments.Should().Be(1, "the Spam comment must not count as pending");
        summary.DueFollowUps.Should().Be(1);
        summary.UnreadSystemAlerts.Should().Be(1);
        summary.PendingAppApprovals.Should().Be(2);
        summary.UnreadFormSubmissions.Should().Be(4);
    }

    [Fact]
    public async Task GetAsync_ShouldSkipAdminOnlyMetrics_WhenIncludeAdminMetricsIsFalse()
    {
        await using var db = await CreateDbAsync();
        var contact = new Contact { FullName = "Jordan Lee", FollowUpDate = DateTimeOffset.UtcNow.AddDays(-1) };
        db.Contacts.Add(contact);
        db.DockerHealthAlerts.Add(new DockerHealthAlert { ContainerName = "gwssuite-web", Message = "Restarted.", IsRead = false });
        await db.SaveChangesAsync();

        var service = new AdminPortalSummaryService(
            new CommentService(db), new FakeContentStudioService(pendingReview: 0), new CrmService(db),
            new DockerHealthService(db), new FakeAppGenerationService(pendingApproval: 5), new FakeFormSubmissionService(unread: 0));

        var summary = await service.GetAsync(includeAdminMetrics: false);

        summary.DueFollowUps.Should().Be(0, "admin-only metrics are skipped, not just zero coincidentally");
        summary.UnreadSystemAlerts.Should().Be(0);
        summary.PendingAppApprovals.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_ShouldNotSkipUnreadFormSubmissions_WhenIncludeAdminMetricsIsFalse()
    {
        // Unlike DueFollowUps/UnreadSystemAlerts/PendingAppApprovals above, form submissions
        // aren't admin-only - a Contributor already sees the per-page list in EditPage.razor
        // and the global inbox, so this count must not go to zero just because the caller
        // (NavMenu.razor for a non-admin) passed includeAdminMetrics: false.
        await using var db = await CreateDbAsync();
        var service = new AdminPortalSummaryService(
            new CommentService(db), new FakeContentStudioService(pendingReview: 0), new CrmService(db),
            new DockerHealthService(db), new FakeAppGenerationService(pendingApproval: 0), new FakeFormSubmissionService(unread: 7));

        var summary = await service.GetAsync(includeAdminMetrics: false);

        summary.UnreadFormSubmissions.Should().Be(7);
    }

    [Fact]
    public async Task GetAsync_ShouldNotShareCachedResults_BetweenTrueAndFalseIncludeAdminMetrics()
    {
        // The actual bug this test guards: NavMenu.razor calls GetAsync(IsAdmin) and
        // Home.razor calls GetAsync(includeAdminMetrics: IsAdmin) within what can be the same
        // Blazor Server circuit-scoped instance. Before the fix, whichever value was requested
        // first got cached forever and the other value silently got the wrong (stale) answer.
        await using var db = await CreateDbAsync();
        var contact = new Contact { FullName = "Jordan Lee", FollowUpDate = DateTimeOffset.UtcNow.AddDays(-1) };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        var service = new AdminPortalSummaryService(
            new CommentService(db), new FakeContentStudioService(pendingReview: 0), new CrmService(db),
            new DockerHealthService(db), new FakeAppGenerationService(pendingApproval: 0), new FakeFormSubmissionService(unread: 0));

        var withoutAdminMetrics = await service.GetAsync(includeAdminMetrics: false);
        var withAdminMetrics = await service.GetAsync(includeAdminMetrics: true);

        withoutAdminMetrics.DueFollowUps.Should().Be(0);
        withAdminMetrics.DueFollowUps.Should().Be(1, "the true-valued call must not reuse the false-valued call's cached answer");
    }

    private sealed class FakeContentStudioService(int pendingReview) : IContentStudioService
    {
        public Task<int> CountPendingReviewAsync(CancellationToken cancellationToken = default) => Task.FromResult(pendingReview);

        public Task<IReadOnlyList<ContentStudioDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult> GenerateArticleAsync(ArticleGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ContentStudioGenerationChunk> GenerateArticleStreamAsync(ArticleGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> RequestRevisionAsync(DraftRevisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> GenerateHeroImageAsync(DraftHeroImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> UploadHeroImageAsync(DraftHeroImageUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentStudioRevisionView>> GetRevisionHistoryAsync(Guid draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContentStudioRevisionDiff?> GetRevisionDiffAsync(Guid draftId, Guid revisionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> RestoreRevisionAsync(DraftRevisionRestoreRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> UpdateDraftMarkdownAsync(DraftMarkdownUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> ApproveDraftAsync(DraftDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> PublishDraftToSiteAsync(DraftPublishRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleGenerationResult?> RejectDraftAsync(DraftDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordAffiliatePlacementInteractionAsync(AffiliatePlacementInteractionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAppGenerationService(int pendingApproval) : IAppGenerationService
    {
        public Task<int> CountPendingApprovalAsync(CancellationToken cancellationToken = default) => Task.FromResult(pendingApproval);

        public Task<IReadOnlyList<AppGenerationRequestView>> ListRequestsAsync(string? status = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppGenerationRequestView?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppGenerationChatResult> StartAsync(StartAppGenerationInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppGenerationChatResult> SendMessageAsync(Guid requestId, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppGenerationChatResult> SubmitForApprovalAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppGenerationChatResult> ApproveAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppGenerationChatResult> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeFormSubmissionService(int unread) : IFormSubmissionService
    {
        public Task<int> CountUnreadAsync(CancellationToken cancellationToken = default) => Task.FromResult(unread);

        public Task<FormSubmission> SubmitAsync(Guid pageId, IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string>? identityFields = null, bool autoCreateContact = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FormSubmission>> ListAsync(Guid pageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FormSubmission>> ListAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FormSubmission>> ListForContactAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LinkToContactAsync(Guid submissionId, Guid contactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FormSubmission?> GetAsync(Guid submissionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkReadAsync(Guid submissionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid submissionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
}
