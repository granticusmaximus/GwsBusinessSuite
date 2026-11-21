using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

// Mail tabs phase 3 (21-11-26): SentinelGPT decides only what the rules weren't sure of, from the sender,
// subject and the rules' reason; an unusable answer leaves the message to be asked again.
public sealed class MailTabAiTests
{
    [Fact]
    public void Parse_ShouldKeepOnlyRealTabsForRealNumbers()
    {
        MailTabAi.Parse("<think>hmm</think>```json\n{\"1\":\"updates\",\"2\":\"Promotions\",\"3\":\"spam\",\"9\":\"social\"}\n```", 3)
            .Should().Equal(new Dictionary<int, string> { [1] = "inbox:updates", [2] = "inbox:promotions" });
        MailTabAi.Parse("not json at all", 2).Should().BeEmpty();
        MailTabAi.Prompt([new MailTabAi.Pending("writer@substack.example", "This   week\nin Georgia", "has an unsubscribe link")])
            .Should().Be("1. from writer@substack.example | subject: This week in Georgia | rules: has an unsubscribe link");
    }

    [Fact]
    public async Task DecideAsync_ShouldSaveTheAiTab_ClearTheSubject_AndDropStaleRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var account = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.MailMessageTabs.AddRange(
            new MailMessageTab { AccountId = account, MessageKey = "<a>", TabKey = "inbox:promotions", Sender = "writer@substack.example", Subject = "This week", CreatedAt = now, CreatedBy = "grant" },
            new MailMessageTab { AccountId = account, MessageKey = "<b>", TabKey = "inbox:promotions", Sender = "orders@store.example", Subject = "Order confirmation - save 10%", CreatedAt = now.AddSeconds(1), CreatedBy = "grant" },
            new MailMessageTab { AccountId = account, MessageKey = "<old>", TabKey = "inbox:primary", Sender = "x@example", Subject = "Old", CreatedAt = now.AddDays(-30), CreatedBy = "grant" });
        await db.SaveChangesAsync();
        var ai = new SortingAi("{\"1\":\"primary\"}");
        var service = new MailTabAiService(db, ai, Options.Create(new ContentStudioOptions { Model = "sentinelgpt" }), TimeProvider.System);

        (await service.DecideAsync(CancellationToken.None)).Should().Be(1);

        ai.Prompts.Should().ContainSingle().Which.Should().Contain("from writer@substack.example").And.Contain("Order confirmation").And.NotContain("Old");
        var rows = await db.MailMessageTabs.AsNoTracking().ToListAsync();
        rows.Should().NotContain(row => row.MessageKey == "<old>", "unclear for 14 days: the message has left the inbox");
        rows.Single(row => row.MessageKey == "<a>").Should().Match<MailMessageTab>(row => row.Source == MailTabSources.Ai && row.TabKey == "inbox:primary" && row.Subject == "");
        rows.Single(row => row.MessageKey == "<b>").Source.Should().Be(MailTabSources.Unclear, "the AI didn't answer for it, so it's asked again");
    }

    private sealed class SortingAi(string answer) : IOllamaService
    {
        public List<string> Prompts { get; } = [];

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            Prompts.Add(userPrompt);
            return Task.FromResult(answer);
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
