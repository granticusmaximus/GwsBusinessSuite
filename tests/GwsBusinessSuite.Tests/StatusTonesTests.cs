using FluentAssertions;
using GwsBusinessSuite.Application.Scoring;
using GwsBusinessSuite.Application.SeoAudit;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Components;

namespace GwsBusinessSuite.Tests;

// One status, one color, on every page (shell redesign R5).
public sealed class StatusTonesTests
{
    [Theory]
    [InlineData(InvoiceStatuses.Draft, StatusTone.Neutral)]
    [InlineData(InvoiceStatuses.Sent, StatusTone.Info)]
    [InlineData(InvoiceStatuses.Paid, StatusTone.Success)]
    [InlineData(InvoiceStatuses.Void, StatusTone.Danger)]
    public void Invoices(string status, StatusTone tone) => StatusTones.ForInvoice(status).Should().Be(tone);

    [Theory]
    [InlineData(SupportTicketStatuses.Open, StatusTone.Info)]
    [InlineData(SupportTicketStatuses.Pending, StatusTone.Warning)]
    [InlineData(SupportTicketStatuses.Resolved, StatusTone.Success)]
    [InlineData(SupportTicketStatuses.Closed, StatusTone.Neutral)]
    public void Tickets(string status, StatusTone tone) => StatusTones.ForTicket(status).Should().Be(tone);

    [Theory]
    [InlineData(SupportTicketPriorities.Urgent, StatusTone.Danger)]
    [InlineData(SupportTicketPriorities.High, StatusTone.Warning)]
    [InlineData(SupportTicketPriorities.Normal, StatusTone.Info)]
    [InlineData(SupportTicketPriorities.Low, StatusTone.Neutral)]
    public void TicketPriorities(string priority, StatusTone tone) => StatusTones.ForTicketPriority(priority).Should().Be(tone);

    [Theory]
    [InlineData(ArticleStatuses.Draft, StatusTone.Neutral)]
    [InlineData(ArticleStatuses.PendingReview, StatusTone.Warning)]
    [InlineData(ArticleStatuses.Published, StatusTone.Success)]
    public void Articles_ShouldFlagWhatWaitsForReview_NotPlainDrafts(string status, StatusTone tone) =>
        StatusTones.ForArticle(status).Should().Be(tone);

    [Fact]
    public void EveryKnownStatus_ShouldHaveADeliberateTone()
    {
        // Lists with an All set: no value may silently fall through to an unintended default.
        InvoiceStatuses.All.Select(StatusTones.ForInvoice).Should().OnlyHaveUniqueItems();
        SupportTicketStatuses.All.Select(StatusTones.ForTicket).Should().OnlyHaveUniqueItems();
        SupportTicketPriorities.All.Select(StatusTones.ForTicketPriority).Should().OnlyHaveUniqueItems();
        ContactStatuses.All.Select(StatusTones.ForContact).Should().Contain([StatusTone.Success, StatusTone.Info, StatusTone.Neutral]);

        StatusTones.ForComment(CommentStatuses.Pending).Should().Be(StatusTone.Warning, "a comment waiting for moderation needs someone");
        StatusTones.ForSeoFinding(SeoAuditFindingStatuses.Fail).Should().Be(StatusTone.Danger);
        StatusTones.ForDealBand(DealScoreBands.Hot).Should().Be(StatusTone.Success);
        StatusTones.ForContainer(isError: true, "running").Should().Be(StatusTone.Danger, "an error outranks the container's state");
        StatusTones.ForAffiliateRelationship("Not Joined").Should().Be(StatusTone.Neutral);
    }

    [Fact]
    public void Stripe_ShouldNameTheToneOrNothing()
    {
        StatusTones.Stripe(StatusTone.Danger).Should().Be("gws-stripe gws-stripe-danger");
        StatusTones.Stripe(null).Should().BeEmpty();
    }

    [Fact]
    public void EveryTone_ShouldHaveItsPillAndStripeStyles()
    {
        var css = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/app.css")));
        foreach (var tone in Enum.GetValues<StatusTone>().Where(tone => tone != StatusTone.Neutral))
            css.Should().Contain($".gws-pill-{StatusTones.CssName(tone)} {{");
        foreach (var tone in Enum.GetValues<StatusTone>())
            css.Should().Contain($".gws-stripe-{StatusTones.CssName(tone)} {{");
    }
}
