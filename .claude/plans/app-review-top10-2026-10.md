# App review: top-10 shortlist (started 18-10-26)

Source: the "GWS Suite App Review" report (https://claude.ai/artifact/3DDhLvvV9abk6KzMBkm2Uf),
which compares each admin page with 5 market alternatives. Grant said "Proceed" on its Top 10.
Each phase is a finished, verified unit; the order puts contained gap fixes first.

**Check before building:** the report was written from the user guides, not a full code read.
Grep for each item first. Two hints already turned up:
- `ContactTimelineService` exists (phase 7 may be partly built).
- The CRM guide mentions Stripe for invoices (phase 2's card payments may exist).

| # | Phase | Status |
|---|-------|--------|
| 1 | Scheduling: per-type time zone, visitor time zones, days ahead / notice / max per day, slot re-check on booking | done 18-10-26 |
| 2 | Billing: multi-currency, reminders (manual + automatic), view/duplicate on sent/paid/void, line order | done 18-10-26 |
| 3 | Support: email-to-ticket (new mail -> ticket, tagged replies join, staff replies emailed with text + Reply-To) | done 18-10-26 |
| 4 | Site: duplicate page, nested menus (dropdowns), hreflang + language switcher + translations on public pages | done 19-10-26 |
| 5 | Automation: expression language (functions, operators, indexing) + sandboxed Code node (Jint) | done 19-10-26 |
| 6 | Media: files on disk (dual-write, 7-day DB grace), resized copies + WebP, srcset, backups include media | done 19-10-26 |
| 7 | Customer timeline: comments, time, reminders, email-origin; ticket/invoice deep links; history beside tickets | done 19-10-26 |
| 8 | Business Intelligence: answer sentence, follow-ups, examples, model choice + JSON mode, 2-min cutoff | done 19-10-26 |
| 9 | One SentinelGPT side panel: every admin page, page + record + selection context, local-first, drafts via Use as reply/note | done 19-10-26 |
| 10 | Nav merge + admin shell redesign (workspace rail, inspector) | not started |

## Phase 1 notes (done)

- `BookingType`: `TimeZoneId` (blank = server zone via `TimeProvider.LocalTimeZone`), `DaysAhead`
  (1-90), `MinNoticeMinutes` (min 15), `MaxPerDay`. `Booking.AttendeeTimeZone`.
- Migration `AddBookingTimeZonesAndRules`: existing types get 14 days / 60 min, matching the old
  fixed behaviour.
- Behaviour change for existing types: their windows used to be read as UTC. They're now read in
  the server zone (New York), so a 09:00-17:00 window means 09:00 New York, as the editor always
  implied.
- Public page detects the browser zone (`wwwroot/js/booking-timezone.js`) and has a picker.
  Emails name the zone.
- Tests: `BookingTimeZoneTests`, covering DST gap, rules, refusing times not on offer, and the
  attendee's zone. The existing `BookingServiceTests` clock is pinned to UTC.
- Still open: calendar sync (Google/iCloud busy times), noted in the CRM guide's limitations.

## Phase 2 notes (done)

- Card payments were already there: Stripe hosted invoice pages, with a webhook marking them paid.
- `InvoiceCurrencies` (Application/Billing) holds 18 currencies and their decimals, used for Stripe
  minor units (JPY = whole units) and for display (`EUR 1,234.50`). `UnitPriceUsd`/`TotalUsd` keep
  their names but are in the invoice's currency.
- Reminders: `Invoice.AutoRemind/RemindersSent/LastReminderAt`. `IStripeInvoicingClient.ResendInvoiceAsync`
  (Stripe's send-invoice email again). Manual sends allowed once per 20 h. Automatic: a day overdue,
  weekly, max 3 (`InvoiceReminders.MaxAutomatic`), via `InvoiceReminderBackgroundService` hourly.
- Fixed along the way:
  - Invoice lines had no stored order: they came back in random GUID order. `InvoiceLineItem.Position` fixes it.
  - Line Add/remove buttons in the Billing and Scheduling editors defaulted to type=submit inside the EditForm.
  - A Stripe error on Send would crash the circuit; now it shows as a message.
- Migration `AddInvoiceRemindersAndLineOrder`.

## Phase 3 notes (done)

- `SupportEmailIn` (Application/Support) holds the rules: ticket tag `[Ticket #<first 8 hex>]`,
  subject cleanup, automatic-mail detection, quoted-reply stripping.
- `SupportEmailInService`: own mailbox (`SupportMailboxSettings`), log table (`SupportInboundEmail`,
  unique MessageId), polled every 2 minutes by `SupportEmailInBackgroundService`.
- IMAP sign-in shared with Sentinel via `ImapMailboxConnector`.
- Security rule: only a DMARC/DKIM-verified sender who is the ticket's contact can add to a ticket.
  Unverified mail still becomes a new ticket, tagged `unverified-sender`, and gets no receipt.
- Staff replies now email the reply text (`TicketReplyEmail` on `IClientPortalEmailSender`), with
  the tag, and with Reply-To set to the mailbox while it's on. Before, the email only had a portal
  link.
- Panel: Support page, "Email-to-ticket" button (`SupportEmailInPanel.razor`).
- Migration `AddSupportEmailToTicket`.

## Phase 4 notes (done)

- Menus: `NavMenuItem.Children` (one level; `HasChildren` is JsonIgnore'd). The header renders a
  dropdown: hover/focus/click, Escape dismisses via `.is-dismissed` (public-site.js). Phones list
  sub-links inline; the footer renders `.footer-group`. Editor: Add sub-link / indent / Move out
  in AppearanceMenus.razor.
- Duplicate: `ICmsBuilderService.DuplicatePageAsync` goes through SavePageAsync (unique slug,
  Draft, canonical blank), with property JSON copied verbatim. Buttons on /admin/pages/all and
  "Duplicate this page" in the editor's navigator (saves first).
- Languages: `PageLanguages` (Application/Localization) builds hreflang, `<html lang>` and the
  switcher; source language "en". `IContentLocalizationService.ListPublishedLanguagesAsync`.
- Found along the way: the real public pages (`RenderPublicCanvasPageAsync`) ignored `?lang`
  entirely; only `/cms/...` and blog posts applied translations. Translated variants now get a
  self canonical. The output cache varies by query, so verified that the languages are cached
  separately.

## Phase 5 notes (done)

- `AutomationExpressions` (Application/Automation) is a parser and evaluator. `ResolveText`, used
  by all ~45 node parameter sites, now calls it. Plain `$json`/`$node` paths behave exactly as
  before: the 223 existing tests passed untouched, and numbers keep their source text. Unparseable
  `{{ }}` text is left literal. `FindErrors` only reports text that mentions `$json`/`$node` or
  calls a function; it runs in `AutomationWorkflowService.ValidateAsync`.
- Code node `core.code`: `IAutomationCodeRunner` (Application) is implemented by
  `JintAutomationCodeRunner` (Infrastructure) on Jint 4.16.4 (4.17.0 was only 12 days old). No
  CLR access. Limits: 3 s, 32 MB, 2M statements, recursion 64, regex 500 ms, 20k chars, 1,000 items
  / 2 MB out. The registry resolves the runner through its serviceProvider, so there's no
  constructor change. The editor shows a plain code textarea for code nodes.
- Fixed along the way: the automation editor's hint printed a literal "{{ExpressionExample}}"
  (Razor doesn't interpolate `{{ }}`).

## Phase 6 notes (done)

- `IMediaFileStore` (Application) is implemented by `FileSystemMediaFileStore` (Infrastructure):
  `Media:StoragePath` (/app/data/media; dev uses media-files/, gitignored), temp-file + move writes,
  and a guard against paths outside the folder. Layout: originals/{2-hex}/{id}.ext, thumbs/{id}.jpg,
  variants/{id}-{versionTicks}-w{width}.ext.
- `MediaAsset.StoragePath/StoredAt/Width/Height`. Uploads and replaces write the files and keep
  DataUri. `MediaStorageBackgroundService` waits 10 min after start (the rollback window), then
  hourly runs MoveToFilesAsync (legacy rows) and ReleaseDatabaseCopiesAsync (after
  `Media:DatabaseCopyGraceDays`=7, and only if the file exists at the right size). Reads prefer the
  file and fall back to DataUri.
- `/media/{id}?w=` snaps to MediaImageSizes; WebP via Accept (not GIF); ETag + Last-Modified ->
  304; Cache-Control public 1h; Vary: Accept. `MediaSrcset.Apply` runs on Layout and the /cms route.
- Backups copy media/originals and media/thumbs. Verify fails if any file-only image (DataUri "")
  is missing from the archive, and reports MediaFileCount.
- AVIF isn't possible with SkiaSharp encode; WebP only. DB pages freed but no VACUUM.
- Live: a 1440px PNG at 193 KB serves as WebP at 70 KB, and the 640 WebP at 23 KB.

## Phase 7 notes (done)

- Most of the timeline already existed (ContactTimelineService + the History card on the contact
  page). Phase 7 filled the gaps rather than rebuilding it.
- New kinds `comment` and `time` (ContactTimelineKinds, with `Icon()` now shared by both pages).
  Comments match on AuthorEmail (case-insensitive, Spam excluded, Pending marked). Time entries
  show duration, unbilled/invoiced/not billable, and "Timer running". Invoices add "Payment
  reminder sent" at LastReminderAt with the RemindersSent count. Tickets tagged exactly "email"
  say "by email".
- `ContactTimelineLinks.Ticket/Invoice` -> `/admin/support?ticket=`, `/admin/billing?invoice=`.
  Support opens the ticket (OnParametersSetAsync, so in-page links work too); Billing expands
  the invoice. Missing ids show "wasn't found". `gwsScroll.toId` (app.js) scrolls only when the
  record isn't in the top half of the first screen, measured against the page because Blazor's
  own scroll-to-top after navigating is smooth and may still be running.
- Support shows a "Customer history" card under the thread: the latest 6 entries, excluding this
  ticket's own, loaded once per ticket opened, with a Full history link.
- Live: checked on desktop and at phone width. Paths tested: direct, from the contact page, and
  same-page ticket switch; plus missing ids and the Billing expand.

## Phase 8 notes (done)

- The Ask box already existed: the local model (via `BrowserLocalOllamaService`, in the browser)
  maps the question onto one catalogued report shape, and the server validates and runs it. It
  stays read-only because the model never sees data and can't write a query. Phase 8 filled the
  gaps.
- `BiAnswerSummary.Describe` writes the one-sentence answer from the result: total, change vs.
  the previous window, leader with its share (or a tie), and high/low for month charts. Numbers
  use en-US, and the page's `FormatValue` now uses it too, so amounts no longer depend on the
  server's culture.
- Follow-ups: `SystemPrompt(..., previous)` gives the model the last plan.
  `KeywordPlan(..., previous)` keeps the source/metric/dimension/range the question doesn't
  mention. Naming a source starts over. Only "by month"/"monthly"/"trend" etc. pick the Month
  breakdown; "this month" is a range.
- `PickModel`: gemma > qwen > llama3 > mistral > phi > any, never "embed" models. The old code
  preferred llama3.2 and could pick `embeddinggemma`. Calls use Ollama JSON mode
  (`GenerateJsonAsync`) with a 2-minute cutoff, then fall back to keywords.
- UI: example question chips, a "New question" button, a follow-up placeholder, and the asked
  question shown with the answer.
- Live (local gemma4): about 30 s cold, 8-14 s warm. Also checked: Ollama unreachable (keywords,
  about 3 s), a model that never answers (cut off at 120 s), and keyword follow-ups.
- Test gotcha: the login limiter allows 5 sign-ins per 15 min per IP, and is in-memory, so an app
  restart resets it. Reuse one browser context instead of signing in per page.

## Phase 9 notes (done)

- Report's definition: one panel docked right that knows the page and record, offers 2-3 context
  actions, shows which model answered (local or server), and never writes silently.
- Found first: the web had no general SentinelGPT chat any more (the standalone page was folded
  into Sentinel in 094fef36). Only Sentinel pages had AI (SentinelAiPanel, SentinelAskPanel).
  The server pieces (coordinator, grounded agent conversation) were reused, not rebuilt.
- `AssistantPanel` (Layout) is an InteractiveServer island in the admin bar, so it survives
  enhanced navigation and keeps the conversation. It's fixed on the right; at 1440px and up
  app.css narrows `.gws-body` via `:has(.gws-assistant)`, and on phones it fills the screen.
- Context: `gwsAssistant.capture()` (app.js) sends the title, h1, `main` innerText (12k) and the
  last selection made outside the panel on the same path. Pages add a record through the scoped
  `AssistantContext` (shared by page and panel on one circuit), which is stamped with the path and
  ignored on any other path. Support (ticket + thread + the customer's other activity, Use as
  reply) and ContactDetail (contact + history, Use as note) register;
  `AssistantDescriptions` builds the text and `AssistantPrompt` caps every part.
- Model: local first through `BrowserLocalOllamaService` + `LocalModels.PickChatModel` (now shared
  with BI). 2-min first-token cutoff, then the server. A failed local probe is skipped for 5 min.
  The server path is `SentinelGptGenerationCoordinator.StartAsync(..., pageContext)`, which goes to
  `StreamAgentConversationAsync(pageContext)`. That context is appended to the model's context
  block only, not stored as the instruction and not used for the searches.
- The side panel never passes a wikiPageId: runs tied to a Sentinel page would surface in that
  page's own panel as unreviewed drafts.
- Answers render through `AssistantMarkdown.ToSafeHtml` (Markdig, HTML off, only http(s)/mailto/
  relative links, images become links). Drafts stay Markdown, which the reply editor takes.
- Not done (deliberately): existing per-page AI tools (BI Ask, Content Studio, the Sentinel page
  panel) were kept rather than removed, since each does a structured job the chat doesn't.
  Inspector sharing is part of Phase 10's shell redesign.
- Live: local gemma4 answered on Support (summary, draft, then Use as reply filled the editor)
  and on a contact and a selection. With localhost:11434 blocked, the server's sentinelgpt
  answered. Checked docking at 1500px, overlay at 1200px, full screen at 390px with no
  horizontal scroll, close/reopen keeping the conversation, and that a ticket draft isn't offered
  on the contact page. Seen: the server model miscounted invoices from flattened page text
  (adjacent badges join as "SentOverdue"), a model limit that's labelled "Server".
