using GwsBusinessSuite.Application.Scoring;
using GwsBusinessSuite.Application.SeoAudit;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Web.Components;

// The five status meanings (shell redesign R5), each with its own color in app.css
// (--gws-status-*): green done, yellow needs attention, red failed or breached, blue open or in
// flight, grey neutral. The brand orange is never a status.
public enum StatusTone { Neutral, Success, Warning, Danger, Info }

// Every status a page shows, mapped to its tone in one place so the same word is the same color
// on every page. StatusPill renders the tone; Stripe gives a row its matching left edge.
public static class StatusTones
{
    public static string CssName(StatusTone tone) => tone.ToString().ToLowerInvariant();

    // A row that needs attention (overdue, breached, failed) gets a stripe in its tone.
    public static string Stripe(StatusTone? tone) => tone is { } value ? $"gws-stripe gws-stripe-{CssName(value)}" : string.Empty;

    public static StatusTone ForInvoice(string status) => status switch
    {
        InvoiceStatuses.Sent => StatusTone.Info,
        InvoiceStatuses.Paid => StatusTone.Success,
        InvoiceStatuses.Void => StatusTone.Danger,
        _ => StatusTone.Neutral
    };

    // A lead or prospect is still in play; a customer is won.
    public static StatusTone ForContact(string status) => status switch
    {
        ContactStatuses.Customer => StatusTone.Success,
        ContactStatuses.Lead or ContactStatuses.Prospect => StatusTone.Info,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForTicket(string status) => status switch
    {
        SupportTicketStatuses.Open => StatusTone.Info,
        SupportTicketStatuses.Pending => StatusTone.Warning,
        SupportTicketStatuses.Resolved => StatusTone.Success,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForTicketPriority(string priority) => priority switch
    {
        SupportTicketPriorities.Urgent => StatusTone.Danger,
        SupportTicketPriorities.High => StatusTone.Warning,
        SupportTicketPriorities.Low => StatusTone.Neutral,
        _ => StatusTone.Info
    };

    // A draft is just unfinished; one waiting for review needs someone.
    public static StatusTone ForArticle(string status) => status switch
    {
        ArticleStatuses.Published => StatusTone.Success,
        ArticleStatuses.PendingReview => StatusTone.Warning,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForArticleDraft(string status) => status switch
    {
        SeoArticleDraftStatuses.PendingReview => StatusTone.Warning,
        SeoArticleDraftStatuses.Approved => StatusTone.Success,
        SeoArticleDraftStatuses.Rejected => StatusTone.Danger,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForPage(string status) =>
        status == CmsPageStatuses.Published ? StatusTone.Success : StatusTone.Neutral;

    public static StatusTone ForComment(string status) => status switch
    {
        CommentStatuses.Approved => StatusTone.Success,
        CommentStatuses.Spam => StatusTone.Danger,
        _ => StatusTone.Warning
    };

    public static StatusTone ForCampaign(string status) => status switch
    {
        EmailCampaignStatuses.Active => StatusTone.Success,
        EmailCampaignStatuses.Paused => StatusTone.Warning,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForEnrollment(string status) => status switch
    {
        EmailCampaignEnrollmentStatuses.Active => StatusTone.Info,
        EmailCampaignEnrollmentStatuses.Completed => StatusTone.Success,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForSubscriber(string status) => status switch
    {
        EmailCampaignSubscriptionStatuses.Confirmed => StatusTone.Success,
        EmailCampaignSubscriptionStatuses.Pending => StatusTone.Warning,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForAnnouncement(string status) => status switch
    {
        ArticleAnnouncementStatuses.Sent => StatusTone.Success,
        ArticleAnnouncementStatuses.Sending or ArticleAnnouncementStatuses.Scheduled => StatusTone.Info,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForSeoFinding(string status) => status switch
    {
        SeoAuditFindingStatuses.Pass => StatusTone.Success,
        SeoAuditFindingStatuses.Warning => StatusTone.Warning,
        _ => StatusTone.Danger
    };

    public static StatusTone ForDealBand(string band) => band switch
    {
        DealScoreBands.Hot => StatusTone.Success,
        DealScoreBands.Warm => StatusTone.Info,
        DealScoreBands.Cool => StatusTone.Warning,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForWorkflow(string status) => status switch
    {
        "Active" => StatusTone.Success,
        "Inactive" => StatusTone.Neutral,
        _ => StatusTone.Warning
    };

    // CJ Ads normalizes the network's wording first (Joined, Active, Approved, Pending, Not Joined).
    public static StatusTone ForAffiliateRelationship(string normalizedStatus) => normalizedStatus switch
    {
        "Joined" or "Active" or "Approved" => StatusTone.Success,
        "Pending" => StatusTone.Warning,
        _ => StatusTone.Neutral
    };

    public static StatusTone ForContainer(bool isError, string state) =>
        isError ? StatusTone.Danger : state == "running" ? StatusTone.Success : StatusTone.Neutral;
}
