using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// "Email me when a new article is posted" campaigns (EmailCampaign.Kind = ArticleAlerts):
// double opt-in subscriptions, and the sweep that announces each newly-live article once.
public sealed class ArticleAlertService(
    IAppDbContext db,
    ICrmService crmService,
    ICmsBuilderService cmsBuilderService,
    IArticleAlertEmailSender emailSender,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ArticleAlertService> logger) : IArticleAlertService
{
    private const string ConfirmPurpose = "GwsBusinessSuite.ArticleAlerts.Confirm.v1";
    private const string UnsubscribePurpose = "GwsBusinessSuite.ArticleAlerts.Unsubscribe.v1";
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromDays(7);
    // Re-submitting the form for a still-pending address re-sends the confirmation at most this
    // often, so the form can't be used to flood someone's inbox.
    private static readonly TimeSpan ConfirmationResendCooldown = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 3;
    // Emails per sweep tick (the sweep runs every minute) - keeps a large list inside typical SMTP
    // provider rate limits.
    private const int SendBudgetPerRun = 40;
    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    public static ArticleAlertSettings ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ArticleAlertSettings();
        try
        {
            return JsonSerializer.Deserialize<ArticleAlertSettings>(json, SettingsJson) ?? new ArticleAlertSettings();
        }
        catch (JsonException)
        {
            return new ArticleAlertSettings();
        }
    }

    private static string SerializeSettings(ArticleAlertSettings settings) => JsonSerializer.Serialize(settings, SettingsJson);

    // ── Campaign management ────────────────────────────────────────────────

    public async Task<ArticleAlertCampaignView> CreateCampaignAsync(string name, string performedBy, CancellationToken cancellationToken = default)
    {
        var campaign = new EmailCampaign
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New article alerts" : name.Trim(),
            Description = "Emails subscribers whenever a new article goes live.",
            Kind = EmailCampaignKinds.ArticleAlerts,
            Status = EmailCampaignStatuses.Draft,
            ArticleAlertSettingsJson = SerializeSettings(new ArticleAlertSettings()),
            CreatedBy = performedBy
        };
        db.EmailCampaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetCampaignAsync(campaign.Id, cancellationToken))!;
    }

    public async Task<ArticleAlertCampaignView?> GetCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.EmailCampaigns.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.Kind == EmailCampaignKinds.ArticleAlerts, cancellationToken);
        if (campaign is null) return null;
        var counts = await db.EmailCampaignSubscriptions.AsNoTracking()
            .Where(s => s.CampaignId == campaignId)
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return new ArticleAlertCampaignView(
            campaign.Id, campaign.Name, campaign.Description, campaign.Status, campaign.ActivatedAt,
            ParseSettings(campaign.ArticleAlertSettingsJson),
            counts.FirstOrDefault(c => c.Status == EmailCampaignSubscriptionStatuses.Confirmed)?.Count ?? 0,
            counts.FirstOrDefault(c => c.Status == EmailCampaignSubscriptionStatuses.Pending)?.Count ?? 0);
    }

    public async Task<ArticleAlertCampaignView> SaveCampaignAsync(Guid campaignId, string name, string description, ArticleAlertSettings settings, string performedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var campaign = await RequireCampaignAsync(campaignId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.FromAddress) && !IsValidEmail(settings.FromAddress))
            throw new ArgumentException("The From address isn't a valid email address.", nameof(settings));
        if (!string.IsNullOrWhiteSpace(settings.ReplyTo) && !IsValidEmail(settings.ReplyTo))
            throw new ArgumentException("The Reply-To address isn't a valid email address.", nameof(settings));

        settings.ExcerptLength = Math.Clamp(settings.ExcerptLength, 40, 600);
        settings.GraceMinutes = Math.Clamp(settings.GraceMinutes, 0, 24 * 60);
        campaign.Name = string.IsNullOrWhiteSpace(name) ? campaign.Name : name.Trim();
        campaign.Description = description?.Trim() ?? string.Empty;
        campaign.ArticleAlertSettingsJson = SerializeSettings(settings);
        campaign.UpdatedAt = timeProvider.GetUtcNow();
        campaign.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return (await GetCampaignAsync(campaignId, cancellationToken))!;
    }

    public async Task<ArticleAlertCampaignView> SetActiveAsync(Guid campaignId, bool active, string performedBy, CancellationToken cancellationToken = default)
    {
        var campaign = await RequireCampaignAsync(campaignId, cancellationToken);
        var wasActive = campaign.Status == EmailCampaignStatuses.Active;
        campaign.Status = active ? EmailCampaignStatuses.Active : EmailCampaignStatuses.Paused;
        // Each (re)activation starts the clock fresh: articles that went live while the list was
        // off are not announced afterwards.
        if (active && !wasActive) campaign.ActivatedAt = timeProvider.GetUtcNow();
        campaign.UpdatedAt = timeProvider.GetUtcNow();
        campaign.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return (await GetCampaignAsync(campaignId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<ArticleAlertSignupCampaignView>> ListSignupCampaignsAsync(CancellationToken cancellationToken = default)
    {
        var campaigns = await db.EmailCampaigns.AsNoTracking()
            .Where(c => c.Kind == EmailCampaignKinds.ArticleAlerts)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Status })
            .ToListAsync(cancellationToken);
        var confirmed = await db.EmailCampaignSubscriptions.AsNoTracking()
            .Where(s => s.Status == EmailCampaignSubscriptionStatuses.Confirmed)
            .GroupBy(s => s.CampaignId)
            .Select(g => new { CampaignId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CampaignId, g => g.Count, cancellationToken);
        return campaigns
            .Select(c => new ArticleAlertSignupCampaignView(c.Id, c.Name, c.Status, confirmed.GetValueOrDefault(c.Id)))
            .ToList();
    }

    // ── Subscribers (double opt-in) ────────────────────────────────────────

    public async Task<ArticleAlertSignupOutcome> SubscribeAsync(Guid campaignId, ArticleAlertSignupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidEmail(email) || email.Length > 254) return ArticleAlertSignupOutcome.InvalidEmail;

        var campaign = await db.EmailCampaigns.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.Kind == EmailCampaignKinds.ArticleAlerts, cancellationToken);
        // Draft lists already accept signups (subscribers wait for launch); paused/archived don't.
        if (campaign is null || campaign.Status is not (EmailCampaignStatuses.Draft or EmailCampaignStatuses.Active))
            return ArticleAlertSignupOutcome.CampaignUnavailable;

        var firstName = (request.FirstName ?? string.Empty).Trim();
        if (firstName.Length > 80) firstName = firstName[..80];
        var contact = await crmService.FindOrCreateContactAsync(
            email, string.IsNullOrWhiteSpace(firstName) ? email : firstName, null, "article-alerts", cancellationToken);

        var now = timeProvider.GetUtcNow();
        var subscription = await db.EmailCampaignSubscriptions
            .FirstOrDefaultAsync(s => s.CampaignId == campaignId && s.ContactId == contact.Id, cancellationToken);
        if (subscription is null)
        {
            subscription = new EmailCampaignSubscription
            {
                CampaignId = campaignId,
                ContactId = contact.Id,
                CreatedBy = "public-signup"
            };
            db.EmailCampaignSubscriptions.Add(subscription);
        }
        else if (subscription.Status == EmailCampaignSubscriptionStatuses.Confirmed)
        {
            // Already in - same neutral answer, nothing sent.
            return ArticleAlertSignupOutcome.CheckInbox;
        }
        else if (subscription.Status == EmailCampaignSubscriptionStatuses.Pending
            && subscription.ConfirmationSentAt is { } sentAt
            && now - sentAt < ConfirmationResendCooldown)
        {
            return ArticleAlertSignupOutcome.CheckInbox;
        }

        subscription.Email = email;
        if (firstName.Length > 0) subscription.FirstName = firstName;
        subscription.Status = EmailCampaignSubscriptionStatuses.Pending;
        subscription.UnsubscribedAt = null;
        subscription.SourcePath = Truncate(request.SourcePath, 300);
        subscription.ConsentText = Truncate(request.ConsentText, 500);
        subscription.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var settings = ParseSettings(campaign.ArticleAlertSettingsJson);
        var branding = await GetBrandingAsync(cancellationToken);
        var token = ConfirmToken(subscription.Id, now + ConfirmationLifetime);
        var rendered = ArticleAlertEmailRenderer.RenderConfirmation(
            settings, subscription.FirstName, $"{PublicBaseUrl}/campaigns/confirm/{token}", branding);
        try
        {
            if (await emailSender.SendAsync(new OutgoingCampaignEmail(
                    settings.FromName, settings.FromAddress, settings.ReplyTo, email,
                    rendered.Subject, rendered.Html, rendered.Text, OneClickUnsubscribeUrl: null), cancellationToken))
            {
                subscription.ConfirmationSentAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The signup is saved; the visitor can re-submit after the cooldown to get a new link.
            logger.LogError(ex, "Couldn't send the article-alert confirmation email for subscription {SubscriptionId}.", subscription.Id);
        }

        return ArticleAlertSignupOutcome.CheckInbox;
    }

    public async Task<ArticleAlertConfirmOutcome> ConfirmAsync(string token, CancellationToken cancellationToken = default)
    {
        Guid subscriptionId;
        try
        {
            // "{subscriptionId}|{expiresUnixSeconds}", signed and encrypted by Data Protection.
            var parts = dataProtectionProvider.CreateProtector(ConfirmPurpose).Unprotect(token ?? string.Empty).Split('|');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out subscriptionId) || !long.TryParse(parts[1], out var expires))
                return ArticleAlertConfirmOutcome.Invalid;
            if (timeProvider.GetUtcNow().ToUnixTimeSeconds() > expires) return ArticleAlertConfirmOutcome.Expired;
        }
        catch (CryptographicException)
        {
            return ArticleAlertConfirmOutcome.Invalid;
        }

        var subscription = await db.EmailCampaignSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
        if (subscription is null) return ArticleAlertConfirmOutcome.Invalid;
        if (subscription.Status == EmailCampaignSubscriptionStatuses.Confirmed) return ArticleAlertConfirmOutcome.AlreadyConfirmed;

        var now = timeProvider.GetUtcNow();
        subscription.Status = EmailCampaignSubscriptionStatuses.Confirmed;
        subscription.ConfirmedAt = now;
        subscription.UnsubscribedAt = null;
        subscription.UpdatedAt = now;
        subscription.UpdatedBy = "subscriber";
        await db.SaveChangesAsync(cancellationToken);
        return ArticleAlertConfirmOutcome.Confirmed;
    }

    public async Task<bool> UnsubscribeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!TryReadUnsubscribeToken(token, out var subscriptionId)) return false;
        var subscription = await db.EmailCampaignSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
        if (subscription is null) return false;
        if (subscription.Status != EmailCampaignSubscriptionStatuses.Unsubscribed)
        {
            var now = timeProvider.GetUtcNow();
            subscription.Status = EmailCampaignSubscriptionStatuses.Unsubscribed;
            subscription.UnsubscribedAt = now;
            subscription.UpdatedAt = now;
            subscription.UpdatedBy = "subscriber";
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<IReadOnlyList<ArticleAlertSubscriberView>> ListSubscribersAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var rows = await db.EmailCampaignSubscriptions.AsNoTracking()
            .Where(s => s.CampaignId == campaignId)
            .ToListAsync(cancellationToken);
        // SQLite can't ORDER BY a DateTimeOffset - sort after materializing.
        return rows.OrderByDescending(s => s.CreatedAt)
            .Select(s => new ArticleAlertSubscriberView(s.Id, s.ContactId, s.Email, s.FirstName, s.Status, s.CreatedAt, s.ConfirmedAt, s.UnsubscribedAt, s.SourcePath))
            .ToList();
    }

    public async Task SetSubscriberStatusAsync(Guid subscriptionId, string status, string performedBy, CancellationToken cancellationToken = default)
    {
        if (status is not (EmailCampaignSubscriptionStatuses.Confirmed or EmailCampaignSubscriptionStatuses.Unsubscribed))
            throw new ArgumentException("Status must be Confirmed or Unsubscribed.", nameof(status));
        var subscription = await db.EmailCampaignSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken)
            ?? throw new InvalidOperationException("That subscriber no longer exists.");
        // Confirming by hand is only allowed for someone who confirmed before and unsubscribed -
        // a Pending address has never proven it wants these emails.
        if (status == EmailCampaignSubscriptionStatuses.Confirmed && subscription.ConfirmedAt is null)
            throw new InvalidOperationException("This address never confirmed its subscription, so it can't be re-added by hand.");

        var now = timeProvider.GetUtcNow();
        subscription.Status = status;
        if (status == EmailCampaignSubscriptionStatuses.Unsubscribed) subscription.UnsubscribedAt = now;
        else subscription.UnsubscribedAt = null;
        subscription.UpdatedAt = now;
        subscription.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArticleAnnouncementView>> ListAnnouncementsAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var rows = await db.ArticleAnnouncements.AsNoTracking()
            .Where(a => a.CampaignId == campaignId)
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(a => a.DueAtUnixSeconds)
            .Select(a => new ArticleAnnouncementView(a.Id, a.ArticleId, a.ArticleTitle, a.Status, a.DueAt, a.CompletedAt, a.RecipientCount, a.DeliveredCount, a.FailedCount))
            .ToList();
    }

    public async Task RetryFailedAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        var announcement = await db.ArticleAnnouncements.FirstOrDefaultAsync(a => a.Id == announcementId, cancellationToken)
            ?? throw new InvalidOperationException("That send no longer exists.");
        var failed = await db.ArticleAnnouncementDeliveries
            .Where(d => d.AnnouncementId == announcementId && !d.Succeeded)
            .ToListAsync(cancellationToken);
        foreach (var delivery in failed) delivery.Attempts = 0;
        if (failed.Count > 0)
        {
            announcement.Status = ArticleAnnouncementStatuses.Sending;
            announcement.CompletedAt = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ── Preview / test ─────────────────────────────────────────────────────

    public async Task<(string Subject, string Html)> RenderPreviewAsync(Guid campaignId, ArticleAlertSettings? unsavedSettings = null, CancellationToken cancellationToken = default)
    {
        var (settings, article, branding) = await PreviewInputsAsync(campaignId, unsavedSettings, cancellationToken);
        var rendered = settings.IsWeeklyDigest
            ? ArticleAlertEmailRenderer.RenderDigest(settings, [article], "Sam", $"{PublicBaseUrl}/campaigns/alerts/unsubscribe/preview", branding)
            : ArticleAlertEmailRenderer.RenderAlert(settings, article, "Sam", $"{PublicBaseUrl}/campaigns/alerts/unsubscribe/preview", branding);
        return (rendered.Subject, rendered.Html);
    }

    public async Task SendTestAsync(Guid campaignId, string toAddress, ArticleAlertSettings? unsavedSettings = null, CancellationToken cancellationToken = default)
    {
        if (!IsValidEmail(toAddress)) throw new ArgumentException("Enter a valid email address for the test.", nameof(toAddress));
        if (!emailSender.IsConfigured) throw new InvalidOperationException("Email isn't set up on this server yet (Smtp__Host), so nothing can be sent.");
        var (settings, article, branding) = await PreviewInputsAsync(campaignId, unsavedSettings, cancellationToken);
        var rendered = settings.IsWeeklyDigest
            ? ArticleAlertEmailRenderer.RenderDigest(settings, [article], "there", $"{PublicBaseUrl}/campaigns/alerts/unsubscribe/test", branding)
            : ArticleAlertEmailRenderer.RenderAlert(settings, article, "there", $"{PublicBaseUrl}/campaigns/alerts/unsubscribe/test", branding);
        await emailSender.SendAsync(new OutgoingCampaignEmail(
            settings.FromName, settings.FromAddress, settings.ReplyTo, toAddress.Trim(),
            "[Test] " + rendered.Subject, rendered.Html, rendered.Text, OneClickUnsubscribeUrl: null), cancellationToken);
    }

    // ── The sweep ──────────────────────────────────────────────────────────

    public async Task<int> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var campaigns = await db.EmailCampaigns.AsNoTracking()
            .Where(c => c.Kind == EmailCampaignKinds.ArticleAlerts && c.Status == EmailCampaignStatuses.Active)
            .ToListAsync(cancellationToken);
        if (campaigns.Count == 0) return 0;

        var liveArticles = await LoadLiveArticlesAsync(now, cancellationToken);
        foreach (var campaign in campaigns)
        {
            await ScheduleNewArticlesAsync(campaign, liveArticles, cancellationToken);
        }

        var nowUnix = now.ToUnixTimeSeconds();
        var due = await db.ArticleAnnouncements
            .Where(a => (a.Status == ArticleAnnouncementStatuses.Scheduled || a.Status == ArticleAnnouncementStatuses.Sending)
                && a.DueAtUnixSeconds <= nowUnix)
            .ToListAsync(cancellationToken);
        var budget = SendBudgetPerRun;
        var sent = 0;
        foreach (var slot in due.GroupBy(a => (a.CampaignId, a.DueAtUnixSeconds)).OrderBy(g => g.Key.DueAtUnixSeconds))
        {
            var campaign = campaigns.FirstOrDefault(c => c.Id == slot.Key.CampaignId);
            if (campaign is null) continue; // paused since it was scheduled - resumes when reactivated
            var isDigest = ParseSettings(campaign.ArticleAlertSettingsJson).IsWeeklyDigest;
            var stop = false;
            foreach (var batch in isDigest ? [slot.ToList()] : slot.Select(a => new List<ArticleAnnouncement> { a }))
            {
                var result = isDigest
                    ? await ProcessDigestAsync(campaign, batch, liveArticles, budget, cancellationToken)
                    : await ProcessAnnouncementAsync(campaign, batch[0], liveArticles, budget, cancellationToken);
                sent += result.Delivered;
                budget -= result.Attempted;
                stop = result.StopAll || budget <= 0;
                if (stop) break;
            }

            if (stop) break;
        }

        return sent;
    }

    private sealed record LiveArticle(Guid Id, string Title, string Slug, string? HeroImageUrl, bool HasStoredHero, DateTimeOffset PublishedAt, string BodyMarkdown, Guid? CategoryId, string Tags);

    private async Task<List<LiveArticle>> LoadLiveArticlesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await db.Articles.AsNoTracking()
            .Where(a => a.TrashedAt == null && a.Status == ArticleStatuses.Published && a.PublishedAt != null)
            .Select(a => new { a.Id, a.Title, a.Slug, a.HeroImageUrl, HasStoredHero = a.HeroImageDataUri != "", a.PublishedAt, a.BodyMarkdown, a.CategoryId, a.Tags, a.Status })
            .ToListAsync(cancellationToken);
        // The same visibility rule the public site uses (scheduled posts aren't live yet).
        return rows
            .Where(a => PublicationWindows.IsVisible(a.Status, ArticleStatuses.Published, a.PublishedAt, now))
            .Select(a => new LiveArticle(a.Id, a.Title, a.Slug, a.HeroImageUrl, a.HasStoredHero, a.PublishedAt!.Value, a.BodyMarkdown, a.CategoryId, a.Tags))
            .ToList();
    }

    private async Task ScheduleNewArticlesAsync(EmailCampaign campaign, List<LiveArticle> liveArticles, CancellationToken cancellationToken)
    {
        if (campaign.ActivatedAt is not { } activatedAt) return;
        var settings = ParseSettings(campaign.ArticleAlertSettingsJson);
        var candidates = liveArticles.Where(a => a.PublishedAt >= activatedAt && MatchesFilter(settings, a)).ToList();
        if (candidates.Count == 0) return;

        var candidateIds = candidates.Select(a => a.Id).ToList();
        var alreadyScheduled = await db.ArticleAnnouncements.AsNoTracking()
            .Where(a => a.CampaignId == campaign.Id && candidateIds.Contains(a.ArticleId))
            .Select(a => a.ArticleId)
            .ToListAsync(cancellationToken);
        foreach (var article in candidates.Where(a => !alreadyScheduled.Contains(a.Id)))
        {
            var afterGrace = article.PublishedAt.AddMinutes(settings.GraceMinutes);
            // Weekly digests: everything published in a week shares the next digest slot.
            var dueAt = settings.IsWeeklyDigest ? settings.NextDigestAt(afterGrace) : afterGrace;
            db.ArticleAnnouncements.Add(new ArticleAnnouncement
            {
                CampaignId = campaign.Id,
                ArticleId = article.Id,
                ArticleTitle = article.Title,
                DueAt = dueAt,
                DueAtUnixSeconds = dueAt.ToUnixTimeSeconds(),
                CreatedBy = "article-alerts"
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record ProcessResult(int Attempted, int Delivered, bool StopAll);

    private async Task<ProcessResult> ProcessAnnouncementAsync(
        EmailCampaign campaign, ArticleAnnouncement announcement, List<LiveArticle> liveArticles, int budget, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var article = liveArticles.FirstOrDefault(a => a.Id == announcement.ArticleId);
        if (announcement.Status == ArticleAnnouncementStatuses.Scheduled)
        {
            if (article is null)
            {
                // Unpublished, trashed or rescheduled during the grace period - don't announce it.
                announcement.Status = ArticleAnnouncementStatuses.Skipped;
                announcement.CompletedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return new ProcessResult(0, 0, StopAll: false);
            }

            var recipients = await db.EmailCampaignSubscriptions.AsNoTracking()
                .Where(s => s.CampaignId == campaign.Id && s.Status == EmailCampaignSubscriptionStatuses.Confirmed)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
            foreach (var subscriptionId in recipients)
            {
                db.ArticleAnnouncementDeliveries.Add(new ArticleAnnouncementDelivery
                {
                    AnnouncementId = announcement.Id,
                    SubscriptionId = subscriptionId,
                    CreatedBy = "article-alerts"
                });
            }
            announcement.RecipientCount = recipients.Count;
            announcement.Status = ArticleAnnouncementStatuses.Sending;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (article is null)
        {
            // Pulled mid-send: stop here; what was already delivered can't be recalled.
            await CompleteAsync(announcement, now, cancellationToken);
            return new ProcessResult(0, 0, StopAll: false);
        }

        var pending = await db.ArticleAnnouncementDeliveries
            .Where(d => d.AnnouncementId == announcement.Id && !d.Succeeded && d.Attempts < MaxAttempts)
            .Take(budget)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            await CompleteAsync(announcement, now, cancellationToken);
            return new ProcessResult(0, 0, StopAll: false);
        }

        var alertArticle = ToAlertArticle(article, campaign);
        var result = await SendPendingAsync(campaign, announcement, pending,
            (settings, subscription, unsubscribeUrl, branding) => ArticleAlertEmailRenderer.RenderAlert(settings, alertArticle, subscription.FirstName, unsubscribeUrl, branding),
            cancellationToken);
        await RefreshCountsAsync(announcement, cancellationToken);
        return result;
    }

    // Weekly digest: every article due in this slot goes out as ONE email per subscriber. The
    // deliveries hang off a single "lead" announcement; the others ride along and complete with it.
    private async Task<ProcessResult> ProcessDigestAsync(
        EmailCampaign campaign, List<ArticleAnnouncement> slot, List<LiveArticle> liveArticles, int budget, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var announcement in slot.Where(a => a.Status == ArticleAnnouncementStatuses.Scheduled && liveArticles.All(l => l.Id != a.ArticleId)))
        {
            announcement.Status = ArticleAnnouncementStatuses.Skipped;
            announcement.CompletedAt = now;
        }
        var members = slot.Where(a => a.Status != ArticleAnnouncementStatuses.Skipped).ToList();
        if (members.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new ProcessResult(0, 0, StopAll: false);
        }

        // The slot's lead is whichever announcement carries the deliveries - possibly one that has
        // already finished (Sent) and so isn't in this sweep's due list.
        var slotDue = members[0].DueAtUnixSeconds;
        var slotIds = await db.ArticleAnnouncements.AsNoTracking()
            .Where(a => a.CampaignId == campaign.Id && a.DueAtUnixSeconds == slotDue)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
        var leadId = await db.ArticleAnnouncementDeliveries.AsNoTracking()
            .Where(d => slotIds.Contains(d.AnnouncementId))
            .Select(d => (Guid?)d.AnnouncementId)
            .FirstOrDefaultAsync(cancellationToken);
        if (leadId is { } existingLead && members.All(m => m.Id != existingLead))
        {
            // The digest already went out; these just ride along with it.
            var finished = await db.ArticleAnnouncements.AsNoTracking().FirstAsync(a => a.Id == existingLead, cancellationToken);
            foreach (var member in members)
            {
                member.Status = ArticleAnnouncementStatuses.Sent;
                member.CompletedAt = now;
                member.RecipientCount = finished.RecipientCount;
                member.DeliveredCount = finished.DeliveredCount;
                member.FailedCount = finished.FailedCount;
            }
            await db.SaveChangesAsync(cancellationToken);
            return new ProcessResult(0, 0, StopAll: false);
        }
        var lead = members.FirstOrDefault(a => a.Id == leadId) ?? members.OrderBy(a => a.ArticleTitle, StringComparer.Ordinal).First();
        if (leadId is null)
        {
            var recipients = await db.EmailCampaignSubscriptions.AsNoTracking()
                .Where(s => s.CampaignId == campaign.Id && s.Status == EmailCampaignSubscriptionStatuses.Confirmed)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
            foreach (var subscriptionId in recipients)
            {
                db.ArticleAnnouncementDeliveries.Add(new ArticleAnnouncementDelivery { AnnouncementId = lead.Id, SubscriptionId = subscriptionId, CreatedBy = "article-alerts" });
            }
            foreach (var member in members)
            {
                member.Status = ArticleAnnouncementStatuses.Sending;
                member.RecipientCount = recipients.Count;
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        var pending = await db.ArticleAnnouncementDeliveries
            .Where(d => d.AnnouncementId == lead.Id && !d.Succeeded && d.Attempts < MaxAttempts)
            .Take(budget)
            .ToListAsync(cancellationToken);
        var articles = members
            .Select(m => liveArticles.FirstOrDefault(l => l.Id == m.ArticleId))
            .Where(a => a is not null)
            .Select(a => ToAlertArticle(a!, campaign))
            .ToList();
        if (pending.Count == 0 || articles.Count == 0)
        {
            await CompleteAsync(lead, now, cancellationToken);
            foreach (var member in members.Where(m => m.Id != lead.Id))
            {
                member.Status = ArticleAnnouncementStatuses.Sent;
                member.CompletedAt = now;
                member.DeliveredCount = lead.DeliveredCount;
                member.FailedCount = lead.FailedCount;
            }
            await db.SaveChangesAsync(cancellationToken);
            return new ProcessResult(0, 0, StopAll: false);
        }

        var result = await SendPendingAsync(campaign, lead, pending,
            (settings, subscription, unsubscribeUrl, branding) => ArticleAlertEmailRenderer.RenderDigest(settings, articles, subscription.FirstName, unsubscribeUrl, branding),
            cancellationToken);
        await RefreshCountsAsync(lead, cancellationToken);
        if (lead.Status == ArticleAnnouncementStatuses.Sent)
        {
            foreach (var member in members.Where(m => m.Id != lead.Id))
            {
                member.Status = ArticleAnnouncementStatuses.Sent;
                member.CompletedAt = timeProvider.GetUtcNow();
                member.DeliveredCount = lead.DeliveredCount;
                member.FailedCount = lead.FailedCount;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return result;
    }

    // Sends one batch of pending deliveries for an announcement - shared by single alerts and
    // digests so opt-out checks, retries and "no mail server" handling stay identical.
    private async Task<ProcessResult> SendPendingAsync(
        EmailCampaign campaign, ArticleAnnouncement announcement, List<ArticleAnnouncementDelivery> pending,
        Func<ArticleAlertSettings, EmailCampaignSubscription, string, ArticleAlertBranding, RenderedEmail> render,
        CancellationToken cancellationToken)
    {
        var subscriptionIds = pending.Select(d => d.SubscriptionId).ToList();
        var subscriptions = await db.EmailCampaignSubscriptions.AsNoTracking()
            .Where(s => subscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);
        var contactIds = subscriptions.Values.Select(s => s.ContactId).ToList();
        var globalOptOuts = await db.Contacts.AsNoTracking()
            .Where(c => contactIds.Contains(c.Id) && c.UnsubscribedFromCampaignsAt != null)
            .Select(c => new { c.Id, c.UnsubscribedFromCampaignsAt })
            .ToDictionaryAsync(c => c.Id, c => c.UnsubscribedFromCampaignsAt!.Value, cancellationToken);

        var settings = ParseSettings(campaign.ArticleAlertSettingsJson);
        var branding = await GetBrandingAsync(cancellationToken);
        AbsolutizeFooter(settings);

        var attempted = 0;
        var delivered = 0;
        foreach (var delivery in pending)
        {
            attempted++;
            if (!subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription)
                || subscription.Status != EmailCampaignSubscriptionStatuses.Confirmed
                // A global "unsubscribe from all campaigns" made after confirming this list wins.
                || (globalOptOuts.TryGetValue(subscription.ContactId, out var optedOutAt) && optedOutAt > (subscription.ConfirmedAt ?? DateTimeOffset.MinValue)))
            {
                delivery.Attempts = MaxAttempts;
                delivery.LastError = "Unsubscribed before this was sent.";
                continue;
            }

            var unsubscribeUrl = $"{PublicBaseUrl}/campaigns/alerts/unsubscribe/{UnsubscribeToken(subscription.Id)}";
            var rendered = render(settings, subscription, unsubscribeUrl, branding);
            try
            {
                var accepted = await emailSender.SendAsync(new OutgoingCampaignEmail(
                    settings.FromName, settings.FromAddress, settings.ReplyTo, subscription.Email,
                    rendered.Subject, rendered.Html, rendered.Text, unsubscribeUrl), cancellationToken);
                if (!accepted)
                {
                    // No mail server configured: leave everything queued (no attempt used up) and
                    // stop the whole sweep until it is.
                    await db.SaveChangesAsync(cancellationToken);
                    return new ProcessResult(attempted, delivered, StopAll: true);
                }

                delivery.Succeeded = true;
                delivery.DeliveredAt = timeProvider.GetUtcNow();
                delivery.Attempts++;
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.Attempts++;
                delivery.LastError = Truncate(ex.Message, 500);
                logger.LogWarning(ex, "Article alert {AnnouncementId} failed for subscription {SubscriptionId} (attempt {Attempt}).",
                    announcement.Id, subscription.Id, delivery.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ProcessResult(attempted, delivered, StopAll: false);
    }

    private async Task CompleteAsync(ArticleAnnouncement announcement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        announcement.Status = ArticleAnnouncementStatuses.Sent;
        announcement.CompletedAt = now;
        await RefreshCountsAsync(announcement, cancellationToken);
    }

    private async Task RefreshCountsAsync(ArticleAnnouncement announcement, CancellationToken cancellationToken)
    {
        var deliveries = await db.ArticleAnnouncementDeliveries.AsNoTracking()
            .Where(d => d.AnnouncementId == announcement.Id)
            .Select(d => new { d.Succeeded, d.Attempts })
            .ToListAsync(cancellationToken);
        announcement.DeliveredCount = deliveries.Count(d => d.Succeeded);
        announcement.FailedCount = deliveries.Count(d => !d.Succeeded && d.Attempts >= MaxAttempts);
        if (announcement.Status == ArticleAnnouncementStatuses.Sending && deliveries.All(d => d.Succeeded || d.Attempts >= MaxAttempts))
        {
            announcement.Status = ArticleAnnouncementStatuses.Sent;
            announcement.CompletedAt ??= timeProvider.GetUtcNow();
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string PublicBaseUrl => (configuration["Canvas:PublicBaseUrl"] is { Length: > 0 } configured
        ? configured
        : "https://grantwatson.dev").TrimEnd('/');

    private string Absolute(string url) =>
        string.IsNullOrWhiteSpace(url) || Uri.TryCreate(url, UriKind.Absolute, out _)
            ? url
            : $"{PublicBaseUrl}/{url.TrimStart('/')}";

    private void AbsolutizeFooter(ArticleAlertSettings settings)
    {
        settings.FooterLogoUrl = Absolute(settings.FooterLogoUrl);
        settings.FooterPhotoUrl = Absolute(settings.FooterPhotoUrl);
    }

    private ArticleAlertArticle ToAlertArticle(LiveArticle article, EmailCampaign campaign)
    {
        var url = $"{PublicBaseUrl}/blog/{Uri.EscapeDataString(article.Slug)}?utm_source=email&utm_medium=article-alert&utm_campaign={Uri.EscapeDataString(Slugify(campaign.Name))}";
        // Email clients don't show data: images, so a stored hero is linked through /og-image.
        var hero = !string.IsNullOrWhiteSpace(article.HeroImageUrl)
            ? Absolute(article.HeroImageUrl)
            : article.HasStoredHero ? $"{PublicBaseUrl}/og-image/{Uri.EscapeDataString(article.Slug)}" : null;
        return new ArticleAlertArticle(article.Title, url, hero, article.PublishedAt, article.BodyMarkdown);
    }

    private static bool MatchesFilter(ArticleAlertSettings settings, LiveArticle article)
    {
        var tags = (settings.TagFilter ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (settings.CategoryIds.Count == 0 && tags.Length == 0) return true;
        if (article.CategoryId is { } categoryId && settings.CategoryIds.Contains(categoryId)) return true;
        var articleTags = (article.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tags.Any(tag => articleTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<(ArticleAlertSettings Settings, ArticleAlertArticle Article, ArticleAlertBranding Branding)> PreviewInputsAsync(
        Guid campaignId, ArticleAlertSettings? unsavedSettings, CancellationToken cancellationToken)
    {
        var campaign = await RequireCampaignAsync(campaignId, cancellationToken);
        var settings = unsavedSettings is not null
            ? ParseSettings(SerializeSettings(unsavedSettings)) // copy, so absolutizing doesn't touch the editor's model
            : ParseSettings(campaign.ArticleAlertSettingsJson);
        AbsolutizeFooter(settings);
        var latest = (await LoadLiveArticlesAsync(timeProvider.GetUtcNow(), cancellationToken))
            .Where(a => MatchesFilter(settings, a))
            .OrderByDescending(a => a.PublishedAt)
            .FirstOrDefault();
        var article = latest is not null
            ? ToAlertArticle(latest, campaign)
            : new ArticleAlertArticle(
                "Your next article's title",
                $"{PublicBaseUrl}/blog",
                null,
                timeProvider.GetUtcNow(),
                "This is where the first lines of your new article appear. Subscribers see the opening of the post, trimmed to a tidy length, with a link to read the whole thing on your site.");
        return (settings, article, await GetBrandingAsync(cancellationToken));
    }

    private async Task<ArticleAlertBranding> GetBrandingAsync(CancellationToken cancellationToken)
    {
        var slug = configuration["Canvas:SiteSlug"] ?? "grantwatson-dev";
        var site = await cmsBuilderService.GetSiteBySlugAsync(slug, cancellationToken);
        var tokens = DesignTokenJson.ParseOrEmpty(site?.DesignTokensJson);
        var accent = tokens.Colors.FirstOrDefault(c => c.Name.Equals("Accent", StringComparison.OrdinalIgnoreCase))?.Hex
            ?? site?.AccentColorHex
            ?? "#2563eb";
        return new ArticleAlertBranding(site?.Name ?? "grantwatson.dev", PublicBaseUrl, accent);
    }

    private async Task<EmailCampaign> RequireCampaignAsync(Guid campaignId, CancellationToken cancellationToken) =>
        await db.EmailCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId && c.Kind == EmailCampaignKinds.ArticleAlerts, cancellationToken)
        ?? throw new InvalidOperationException("That article-alert campaign no longer exists.");

    private string ConfirmToken(Guid subscriptionId, DateTimeOffset expiresAt) =>
        dataProtectionProvider.CreateProtector(ConfirmPurpose).Protect($"{subscriptionId:N}|{expiresAt.ToUnixTimeSeconds()}");

    private string UnsubscribeToken(Guid subscriptionId) =>
        dataProtectionProvider.CreateProtector(UnsubscribePurpose).Protect(subscriptionId.ToString("N"));

    private bool TryReadUnsubscribeToken(string token, out Guid subscriptionId)
    {
        subscriptionId = Guid.Empty;
        try
        {
            return Guid.TryParse(dataProtectionProvider.CreateProtector(UnsubscribePurpose).Unprotect(token ?? string.Empty), out subscriptionId);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool IsValidEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value.Trim(), out var parsed) && parsed.Address == value.Trim();

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static string Slugify(string value) =>
        new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
