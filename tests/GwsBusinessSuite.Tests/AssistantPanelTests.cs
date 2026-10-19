using FluentAssertions;
using GwsBusinessSuite.Application.Assistant;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Components;

namespace GwsBusinessSuite.Tests;

// The SentinelGPT side panel: what it tells the model about the page, and which page a record
// description belongs to.
public sealed class AssistantPanelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Suggestions_ShouldPutTheSelectionFirst_AndKeepThree()
    {
        AssistantPrompt.Suggestions(null, hasSelection: false).Should().Equal("Summarize this page", "What needs my attention here?");
        var page = new AssistantPageContext("Support", Suggestions: ["Draft a reply", "Summarize this thread", "What does the customer need?"]);
        AssistantPrompt.Suggestions(page, hasSelection: true).Should().Equal("Explain the selected text", "Draft a reply", "Summarize this thread");
    }

    [Fact]
    public void DescribeContext_ShouldPreferRecordDetailsOverPageText_AndAlwaysIncludeTheSelection()
    {
        var capture = new AssistantCapture("Support - GWS", "Support", "  the PDF link\r\n gives a blank page ", "Ticket list\n\n\n\nInvoice PDF won't open");
        var generic = AssistantPrompt.DescribeContext(null, capture);
        generic.Should().StartWith("Page: Support\n").And.Contain("What the page shows").And.Contain("Ticket list\n\nInvoice PDF", "runs of blank lines are squeezed");
        generic.Should().EndWith("Text the user selected:\nthe PDF link\ngives a blank page");

        var record = new AssistantPageContext("Support", "Ticket", "Invoice PDF won't open", "Subject: Invoice PDF won't open");
        var described = AssistantPrompt.DescribeContext(record, capture);
        described.Should().Contain("Open record: Ticket \"Invoice PDF won't open\"").And.Contain("Record details:").And.NotContain("What the page shows");
        described.Should().Contain("the PDF link");

        AssistantPrompt.DescribeContext(null, new AssistantCapture("", null, null, new string('x', 10_000)))
            .Should().StartWith("Page: an admin page").And.EndWith("x …").And.HaveLength("Page: an admin page\nWhat the page shows (may be cut short):\n".Length + AssistantPrompt.MaxVisibleText + 2);
    }

    [Fact]
    public void ForLocalModel_ShouldCarryTheRecentTurns_AndThePageInTheSystemPrompt()
    {
        var history = Enumerable.Range(1, 8).Select(i => new AssistantTurn($"Q{i}", i == 8 ? new string('a', 2_000) : $"A{i}")).ToList();
        var (system, prompt) = AssistantPrompt.ForLocalModel("Page: CRM", history, "  And now?  ");

        system.Should().StartWith(AssistantPrompt.SystemPrompt).And.EndWith("## Current page\nPage: CRM");
        prompt.Should().NotContain("Q2\n").And.StartWith("User: Q3\nAssistant: A3", "only the latest six turns travel");
        prompt.Should().Contain(new string('a', AssistantPrompt.MaxHistoryAnswer) + " …").And.EndWith("User: And now?");
    }

    [Fact]
    public void TicketDescription_ShouldIncludeTheLatestThread_AndTheCustomersOtherActivity()
    {
        var messages = Enumerable.Range(1, 10).Select(i => new SupportTicketMessageView(Guid.NewGuid(),
            i % 2 == 0 ? SupportTicketAuthorTypes.Staff : SupportTicketAuthorTypes.Contact, i % 2 == 0 ? "grant" : "Bill",
            $"message {i}\n  with   spacing", T0.AddMinutes(i), [])).Reverse().ToList();
        var ticket = new SupportTicketView(Guid.NewGuid(), Guid.NewGuid(), "Bill Tester", "Invoice PDF won't open", "Open", "High", "grant",
            null, null, messages, T0, "email", null, null, null, null);
        var activity = Enumerable.Range(1, 14).Select(i => new ContactTimelineEntry(T0.AddDays(-i), ContactTimelineKinds.Invoice, $"Invoice {i}", i == 1 ? "$900.00" : null, null)).ToList();

        var context = AssistantDescriptions.Ticket(ticket, activity);

        (context.PageName, context.RecordKind, context.RecordTitle).Should().Be(("Support", "Ticket", "Invoice PDF won't open"));
        context.Suggestions.Should().Contain("Draft a reply to the customer");
        var details = context.Details!;
        details.Should().Contain("Status: Open · Priority: High · Assigned to: grant · Tags: email").And.Contain("Customer: Bill Tester");
        details.Should().Contain("the latest 8 of 10 messages").And.NotContain("message 2 ").And.Contain("Bill (customer): message 3 with spacing").And.Contain("grant (staff): message 10 with spacing");
        details.IndexOf("message 3", StringComparison.Ordinal).Should().BeLessThan(details.IndexOf("message 10", StringComparison.Ordinal), "oldest first");
        details.Should().Contain("Invoice 1 ($900.00)").And.Contain("Invoice 12").And.NotContain("Invoice 13").And.EndWith("…and 2 older entries");
    }

    [Fact]
    public void ContactDescription_ShouldListTheContactAndTheirHistory()
    {
        var contact = new Contact { FullName = "Dana Client", Email = "dana@example.com", Company = "Acme", Status = ContactStatuses.Lead };
        var context = AssistantDescriptions.Contact(contact, [new ContactTimelineEntry(T0, ContactTimelineKinds.Note, "Note", "Intro call went well", null)]);
        context.RecordTitle.Should().Be("Dana Client");
        context.Details.Should().Contain("Email: dana@example.com").And.Contain("Company: Acme").And.Contain("History (newest first):").And.Contain("Note (Intro call went well)");
    }

    [Fact]
    public void AssistantContext_ShouldOnlyApplyOnThePageThatSetIt()
    {
        var navigation = new TestNavigation("http://localhost/admin/support?ticket=1");
        var assistant = new AssistantContext(navigation);
        var changes = 0;
        assistant.Changed += () => changes++;

        assistant.Set(new AssistantPageContext("Support", "Ticket", "A"), _ => Task.CompletedTask, "Use as reply");
        assistant.Current!.ApplyLabel.Should().Be("Use as reply");
        navigation.Go("http://localhost/admin/support?ticket=2#thread");
        assistant.Current.Should().NotBeNull("the query string and fragment don't make it another page");
        navigation.Go("http://localhost/admin/crm");
        assistant.Current.Should().BeNull("a page that sets nothing doesn't inherit the last record");
        navigation.Go("http://localhost/admin/Support/");
        assistant.Current.Should().NotBeNull();

        assistant.Clear();
        assistant.Current.Should().BeNull();
        changes.Should().Be(2);
        assistant.Set(new AssistantPageContext("Support"));
        assistant.Current!.Apply.Should().BeNull();
        assistant.Current.ApplyLabel.Should().BeNull();
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);

        public void Go(string uri) => Uri = uri;

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }
}

public sealed class AssistantMarkdownTests
{
    [Fact]
    public void ToSafeHtml_ShouldRenderMarkdown_ButNoRawHtmlOrScriptLinks()
    {
        var html = AssistantMarkdown.ToSafeHtml("There are **2** invoices:\n\n1. Website build\n2. Retainer\n\n<script>alert(1)</script>\n\n[open](/admin/billing) [bad](javascript:alert(1)) [web](https://example.com) ![pixel](https://tracker.example/p.png)");

        html.Should().Contain("<strong>2</strong>").And.Contain("<ol>").And.Contain("<li>Website build</li>");
        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
        html.Should().Contain("href=\"/admin/billing\"").And.Contain("href=\"https://example.com\"").And.Contain("href=\"#\"").And.NotContain("javascript:");
        html.Should().NotContain("<img").And.Contain("href=\"https://tracker.example/p.png\"");
        AssistantMarkdown.ToSafeHtml("//evil.example/x and [x](//evil.example)").Should().Contain("href=\"#\"");
    }
}
