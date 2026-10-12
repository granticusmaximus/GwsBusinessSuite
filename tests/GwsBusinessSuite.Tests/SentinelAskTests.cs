using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 2, "Ask the workspace": answers come only from pages the asker can view, and cite them.
public sealed class SentinelAskTests
{
    private static WikiBlock Paragraph(string text) =>
        new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());

    private static SentinelAskContext Context(int sources) =>
        new("What is the refund policy?", Enumerable.Range(1, sources)
            .Select(n => new SentinelAskSource(n, Guid.NewGuid(), false, null, $"Page {n}", "text", n == 1, 50)).ToList());

    [Fact]
    public void KeyTerms_ShouldDropFillerWords_LongestFirst()
    {
        SentinelAsk.KeyTerms("What's our refund policy for annual plans?").Should().Equal("refund", "policy", "annual", "plans");
        SentinelAsk.KeyTerms("who is it?").Should().BeEmpty();
        SentinelAsk.Normalize("  many   spaces \n here ").Should().Be("many spaces here");
    }

    [Fact]
    public void Excerpt_ShouldKeepTheOpeningAndTheMatchingPassages_InPageOrder_WithinTheBudget()
    {
        var blocks = new[]
        {
            Paragraph("Customer policies for GWS."),
            Paragraph("Shipping takes five days."),
            Paragraph("Refunds for annual plans are pro-rated."),
            Paragraph(new string('x', 500)),
            Paragraph("Monthly plan refunds are not offered.")
        };

        var excerpt = SentinelAsk.Excerpt(blocks, ["refunds", "annual"], budget: 200);

        excerpt.Should().Be("Customer policies for GWS.\nShipping takes five days.\nRefunds for annual plans are pro-rated.\nMonthly plan refunds are not offered.");
    }

    [Fact]
    public void Clean_ShouldKeepRealCitations_DropInventedOnes_AndRecogniseNotFound()
    {
        var context = Context(2);

        var answer = SentinelAsk.Clean("Annual plans are refunded pro rata [1, 7]. Monthly plans are not [3].", context);
        answer.Text.Should().Be("Annual plans are refunded pro rata [1]. Monthly plans are not.");
        answer.Cited.Should().Equal(1);
        answer.NotFound.Should().BeFalse();

        SentinelAsk.Clean("NOT FOUND", context).NotFound.Should().BeTrue();
        SentinelAsk.Clean("not found.", context).NotFound.Should().BeTrue();
        SentinelAsk.Clean("Anything at all [1].", Context(0)).NotFound.Should().BeTrue("there were no sources to answer from");
        SentinelAsk.Clean("Refunds are pro rata.", context).Uncited.Should().BeTrue();
        SentinelAsk.SplitCitations("Refunds take 14 days [1, 2]. Done").Should().HaveCount(3);
        SentinelAsk.SplitCitations("Refunds take 14 days [1, 2]. Done")[1].Numbers.Should().Equal(1, 2);
        SentinelAsk.SplitCitations("No citations").Should().ContainSingle();
        SentinelAsk.UserPrompt(context).Should().Contain("[1] Page 1 (verified)\ntext").And.Contain("[2] Page 2\ntext");
    }

    [Fact]
    public async Task Ask_ShouldOnlyUsePagesTheAskerCanView_AndLogEveryQuestion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        var policy = new WikiPage
        {
            Title = "Refund policy", Slug = "refund-policy",
            BlocksJson = WikiBlockJson.Serialize([Paragraph("Refunds for annual plans are pro-rated."), Paragraph("Monthly plans have no refunds.")])
        };
        var secret = new WikiPage
        {
            Title = "Refund exceptions (board only)", Slug = "refund-exceptions",
            BlocksJson = WikiBlockJson.Serialize([Paragraph("Refunds for annual plans can be waived for the board's friends.")])
        };
        db.WikiPages.AddRange(policy, secret);
        await db.SaveChangesAsync();
        db.SentinelResourcePermissions.AddRange(
            new SentinelResourcePermission { TargetId = policy.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View },
            new SentinelResourcePermission { TargetId = secret.Id, IsDatabase = false, Username = "grant", AccessLevel = SentinelAccessLevels.FullAccess });
        await db.SaveChangesAsync();
        var ollama = new FakeOllama { Response = "Annual plans are refunded pro rata [1]. Exceptions exist [2]." };
        var access = new SentinelAccessService(db);
        var service = new SentinelAskService(db, new SentinelWorkspaceService(db, TimeProvider.System, access), ollama,
            new FakeSettings(), TimeProvider.System, NullLogger<SentinelAskService>.Instance);

        var context = await service.PrepareAsync("What is the refund policy for annual plans?", "sam");
        context.Sources.Select(s => s.Title).Should().Equal("Refund policy");
        context.Sources[0].Excerpt.Should().Contain("pro-rated");
        (await service.PrepareAsync("annual plans", "grant")).Sources.Select(s => s.Title).Should().Contain("Refund exceptions (board only)", "grant can see it");

        var answer = await service.AnswerOnServerAsync(context);
        answer.Text.Should().Be("Annual plans are refunded pro rata [1]. Exceptions exist.", "a source number that wasn't given can't be cited");
        answer.Cited.Should().Equal(1);
        ollama.UserPrompts.Single().Should().NotContain("board").And.Contain("Refund policy");

        var nothing = await service.PrepareAsync("board friends waiver", "sam");
        nothing.HasSources.Should().BeFalse("the only page about it is one sam can't see");
        (await service.AnswerOnServerAsync(nothing)).NotFound.Should().BeTrue();
        ollama.UserPrompts.Should().HaveCount(1, "no model call without sources");

        await service.RecordAsync(context, answer, "sam", "local", "gemma4");
        await service.RecordAsync(nothing, SentinelAsk.NotFound(), "sam", "server", "none");
        var logged = await db.SentinelQuestions.AsNoTracking().OrderBy(q => q.AskedUnix).ThenBy(q => q.NotFound).ToListAsync();
        logged.Should().HaveCount(2);
        logged[0].Should().Match<SentinelQuestion>(q => q.Username == "sam" && !q.NotFound && q.AnsweredBy == "local" && q.Model == "gemma4" && q.SourceCount == 1 && q.Terms == "refund policy annual plans");
        JsonDocument.Parse(logged[0].SourcesJson).RootElement[0].GetProperty("cited").GetBoolean().Should().BeTrue();
        logged[1].NotFound.Should().BeTrue();
        await service.Invoking(s => s.PrepareAsync("  ", "sam")).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeSettings : ISiteSettingsService
    {
        public Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteSettingsView(10, null, null, null, null, null, 10));
        public Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOllama : IOllamaService
    {
        public string Response { get; set; } = string.Empty;
        public List<string> UserPrompts { get; } = [];

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            UserPrompts.Add(userPrompt);
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }
}
