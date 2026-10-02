# Plan: "New article" email alerts + reusable signup widget

Status: **decisions made 2026-10-02 - building** (see "Decisions" at the bottom).

## Goal

Visitors subscribe (verified email only) to be emailed when a new article goes live on
grantwatson.dev. Grant edits the email's message; the article card (title, publish date/time,
hero image, ~180-character excerpt with "… (Read More)") is filled in automatically, with a
branded footer (logo, "Grant Watson Software", a photo of Grant from the media library). The
signup form is a page-editor widget that can be dropped on any page; dropping it opens a modal to
pick which campaign it feeds.

## What already exists (verified 2026-10-02) - reuse, don't rebuild

- `EmailCampaign` / `EmailCampaignStep` / `EmailCampaignEnrollment` / `EmailCampaignSendLog` -
  **drip sequences** (steps with `DelayDays`, contacts enrolled, `ProcessDueSendsAsync` loop).
  `/admin/email-campaigns` UI. Article alerts are event-driven, not delay-driven, so they are a
  new campaign *kind* beside drips, sharing the list page, sender and unsubscribe plumbing.
- `Contact.UnsubscribedFromCampaignsAt` + data-protected unsubscribe tokens + the
  `/campaigns/unsubscribe/{token}` route (`EmailCampaignService.UnsubscribeByTokenAsync`).
- `EmailCampaignEmailSender` (MailKit) - now falls back to the shared `Smtp__*` section.
- `/og-image/{slug}` serves an article's stored hero (`HeroImageDataUri`) as a real image - email
  clients (Gmail/Outlook) don't render `data:` images, so emails use `HeroImageUrl` or this route.
- `/media/{id}` public media URLs (logo, Grant's photo).
- CMS `form` widget + `CmsFormSubmissionEndpoint` (honeypot, rate limiting) and Turnstile
  (`TurnstileService`) - the subscribe endpoint reuses both.
- `PublicationWindows.IsVisible` / `IsArticlePubliclyVisible` - the single rule for "is this
  article live", including scheduled future publish times.

## Design

### 1. Campaign kind: "New article alerts"

`EmailCampaign` gains `Kind` (`Sequence` = today's drips, default; `ArticleAlerts`) plus the
alert fields (new columns, one migration):

- **Sender**: `FromName` (default "Grant Watson Software"), `FromAddress` (default
  grant@gwsapp.net), optional `ReplyTo`.
- **Subject template** with tokens - default `New post: {{article.title}}`.
- **Message** (rich text, the part Grant writes) with tokens: `{{subscriber.firstName}}`,
  `{{article.title}}`, `{{article.publishedAt}}`. Shown above the article card.
- **Article card** (automatic, not editable copy): hero image, title, published date + time in
  the site's time zone, excerpt = first 180 characters of the body with Markdown stripped, cut at a
  word boundary, then "…" and a **(Read More)** link. Options: show/hide hero; excerpt length
  (default 180).
- **Footer**: logo (media picker, default site logo), brand line "Grant Watson Software", photo
  (media picker), optional short sign-off, social links (reuses the site footer nav), the required
  unsubscribe link, and a **mailing address** field (CAN-SPAM requires a postal address in
  commercial email - a PO box is fine).
- **Activation stamp**: `ActivatedAt` - only articles that go live *after* activation are sent, so
  switching a campaign on never blasts the back catalog.
- **Optional filter**: only articles in chosen categories/tags (empty = every article).

### 2. Subscribers with double opt-in ("verified email only")

New `EmailCampaignSubscription` (CampaignId, ContactId, Status `Pending` / `Confirmed` /
`Unsubscribed`, ConfirmedAt, consent evidence: source page path, consent text shown, timestamp).

1. Visitor submits the widget -> Turnstile + honeypot + rate limit -> a Contact is found or
   created by email (existing unique-email rule) -> subscription `Pending` -> a **confirmation
   email** ("Confirm your subscription") with a signed, expiring (7-day) link.
2. Clicking it -> `Confirmed` -> a themed "You're subscribed" page. Only `Confirmed` subscribers
   are ever sent alerts. Re-submitting an already-confirmed address shows the same neutral
   "check your inbox" message (no "already subscribed" leak).
3. Every alert carries a one-click unsubscribe link **and** `List-Unsubscribe` +
   `List-Unsubscribe-Post` headers (RFC 8058 - Gmail/Yahoo bulk-sender requirement since 2024).
   Unsubscribing affects that campaign; the existing global opt-out still wins everywhere.
4. Admin: per-campaign subscriber list (status, confirmed date, source page), manual
   remove/resubscribe, CSV export. Subscriptions join Privacy Operations' export/erasure.

### 3. Trigger: when an article goes live

A background sweep (every minute, `OllamaWorkloadScheduler`-style background service pattern)
finds, for each **Active** article-alerts campaign, articles that are publicly visible, went live
after `ActivatedAt`, match the filter, and have no `ArticleAnnouncement` row yet. Covers every
publish path (Posts editor, Content Studio, developer API) and scheduled posts, because it keys off
"is it live now" rather than a save button.

- `ArticleAnnouncement` (CampaignId, ArticleId, Status, counts) - unique per pair, so an article is
  announced at most once even across restarts.
- Per-recipient `EmailCampaignSendLog` rows (reused) - retried on transient SMTP failure, never
  re-sent once delivered. Sends throttled (small batches) to stay inside provider limits.
- **Send mode** (decision below): automatic, optionally after a grace delay, or held for
  "Review & send".

### 4. The email itself

- Table-based, inline-styled HTML (what email clients actually support), 600px, mobile-friendly,
  with a plain-text alternative. Colors from the site's theme tokens so it matches grantwatson.dev.
- All URLs absolute (`Canvas:PublicBaseUrl`). "Read More" links to
  `/blog/{slug}?utm_source=email&utm_medium=article-alert&utm_campaign={campaign}` so clicks show
  up in Growth analytics. No open-tracking pixel by default (privacy; Apple Mail inflates it anyway).
- **Live preview** in the editor using the latest real article, and **Send test to me**.

### 5. Signup widget (reusable on any page)

- New page-editor widget **Email signup** (`email-signup`): heading, description, optional
  first-name field, email field, button label, consent line, success message, and `campaignId`.
- Dropping it on a page opens a **"Choose an email campaign" modal**: dropdown of eligible
  campaigns (Active or Draft campaigns that accept signups), a summary of the selected one
  (subscriber count, what it sends), and **Create a new campaign**. The inspector keeps a "Change
  campaign" control; a widget whose campaign was deleted shows a clear editor-only warning.
- Renders server-side, theme-aware, CSP-safe (external JS only, like `contact-form.js`), Turnstile
  inline. Also offered as a section template ("Newsletter signup" updated to use it) and works as a
  **Global block** for site-wide reuse; the Blog / News pre-built page uses it.

### 6. Admin experience

`/admin/email-campaigns` -> **New campaign** asks the kind. The article-alerts editor has tabs:
**Message** (subject, rich text, tokens, footer pickers) with side-by-side live preview; **Settings**
(sender, filter, send mode, mailing address); **Subscribers**; **Sends** (each article
announcement: when, delivered/failed, retry failed). A banner warns when SMTP or Turnstile isn't
configured, since nothing can be delivered or collected until it is.

## Prerequisites outside the code (Grant)

- `Smtp__*` set in the droplet `.env` with a provider that can send **as grant@gwsapp.net** - the
  gwsapp.net domain needs SPF + DKIM (+ DMARC) records for that provider, or alerts land in spam.
- Turnstile keys (`Turnstile__SiteKey` / `Turnstile__SecretKey`) - the widget uses the same check.

## Phases (each verified with `verify-release.sh` before the next)

1. Data model + migration (campaign kind/fields, subscriptions, announcements) + service API.
2. Subscribe / confirm / unsubscribe endpoints, confirmation + result pages, RFC 8058 headers.
3. Email renderer (HTML + text, excerpt rule, absolute URLs) + campaign editor UI, preview, test send.
4. Article-live sweep + throttled sending + Sends tab + retry.
5. `email-signup` widget + campaign picker modal + section template / global block / Blog template.
6. Docs (`EMAIL_CAMPAIGNS` / user guides), unit tests (excerpt, tokens, idempotency, opt-in
   states), browser tests (drop widget -> modal -> subscribe -> confirm -> publish -> email in a
   pickup directory).

## Decisions (Grant, 2026-10-02)

1. **Send timing: 15-minute grace.** An alert sends automatically 15 minutes after its article
   goes live (`ArticleAlertGraceMinutes`, default 15), so a typo can be fixed or the post
   unpublished first - if the article is no longer live when the grace ends, nothing is sent.
2. **Articles: all, with an optional per-campaign category/tag filter.**
3. **Name: optional first name** on the form; emails greet "Hi {name}," or fall back to "Hi there,".
