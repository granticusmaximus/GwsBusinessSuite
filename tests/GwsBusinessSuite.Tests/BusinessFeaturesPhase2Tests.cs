using FluentAssertions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

// 19-11-26, five business features, phase 2: the cash-flow forecast and articles to refresh.
public sealed class BusinessFeaturesPhase2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static Invoice Invoice(Guid contact, string title, decimal amount, DateTimeOffset due, DateTimeOffset? paid = null) => new()
    {
        ContactId = contact, Title = title, DueDate = due, PaidAt = paid,
        Status = paid is null ? InvoiceStatuses.Sent : InvoiceStatuses.Paid,
        LineItems = [new InvoiceLineItem { Description = "Work", Quantity = 1, UnitPriceUsd = amount }],
    };

    [Fact]
    public void CashFlow_ShouldMoveInvoicesByHowLateTheClientPays_AndWeightDealsByStage()
    {
        var slow = Guid.NewGuid();
        var prompt = Guid.NewGuid();
        var invoices = new[]
        {
            Invoice(slow, "Old 1", 100, Now.AddDays(-60), Now.AddDays(-40)),   // paid 20 days late
            Invoice(slow, "Old 2", 100, Now.AddDays(-90), Now.AddDays(-70)),   // paid 20 days late
            Invoice(slow, "Open slow", 1000, Now.AddDays(3)),                  // expected in ~23 days
            Invoice(prompt, "Open prompt", 500, Now.AddDays(3)),               // expected this week
            Invoice(prompt, "Overdue", 200, Now.AddDays(-5)),                  // counted now
        };
        var deals = new[] { new Deal { ContactId = prompt, Title = "Big", Stage = DealStages.ProposalSent, ValueUsd = 4000, ExpectedCloseDate = Now.AddDays(10) } };
        var names = new Dictionary<Guid, string> { [slow] = "Slow Co", [prompt] = "Prompt Co" };

        var result = CashFlowForecast.Build(invoices, deals, names, Now);

        result.FromInvoices.Should().Be(1700);
        result.FromDeals.Should().Be(2000, "a proposal counts at half");
        result.OverdueInvoices.Should().Be(1);
        result.Weeks[0].FromInvoices.Should().Be(700, "the prompt client's invoice and the overdue one land this week");
        result.Weeks.Single(w => w.Items.Any(i => i.Label.StartsWith("Open slow"))).Start.Should().BeOnOrAfter(new DateTimeOffset(Now.Date.AddDays(21), TimeSpan.Zero));
        result.Weeks.Single(w => w.FromDeals > 0).Start.Should().BeOnOrAfter(new DateTimeOffset(Now.Date.AddDays(35), TimeSpan.Zero), "deals pay 30 days after closing");

        var late = CashFlowForecast.Build(invoices, deals, names, Now, prompt, 30);
        late.Weeks[0].FromInvoices.Should().Be(0, "what if Prompt Co pays 30 days late");
    }

    [Fact]
    public void ArticleRefresh_ShouldFlagFallingAndStaleArticles_Only()
    {
        Article Published(string slug, DateTimeOffset at) => new() { Slug = slug, Title = slug, Status = ArticleStatuses.Published, PublishedAt = at };
        var falling = Published("falling", Now.AddDays(-100));
        var steady = Published("steady", Now.AddDays(-100));
        var old = Published("old", Now.AddDays(-500));
        var draft = new Article { Slug = "draft", Title = "draft", Status = ArticleStatuses.Draft };
        var last30 = new Dictionary<string, int> { ["/blog/falling"] = 10, ["/blog/steady"] = 90 };
        var prior30 = new Dictionary<string, int> { ["/blog/falling"] = 80, ["/blog/steady"] = 100 };

        var found = ArticleRefresh.Find([falling, steady, old, draft], last30, prior30, Now);

        found.Select(c => c.Slug).Should().Equal("falling", "old");
        found[0].Reason.Should().Contain("fell 88%");
        found[1].Reason.Should().Contain("Not updated for 16 months");
        ArticleRefresh.Prompt(old, Now).Should().Contain("Title: old");
    }
}
