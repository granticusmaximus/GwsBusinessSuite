# Handoff: "Make it production ready" — punch-list work in progress

## Context

Repo: `/Users/grantwatson/Desktop/Development/CSharp/GwsBusinessSuite` (Blazor Server). CLAUDE.md
requires running `./scripts/verify-release.sh` before ending any turn that changed code — treat
every edit as effectively shipped, not staged for review. This repo does **not** auto-commit;
the user commits/pushes manually (sometimes outside the session) — do not assume clean `git
status` means nothing changed, and do not commit unless asked.

User's ask, in sequence across this session: "make this application completely done, production
ready done" → scoped via AskUserQuestion to **"finish partially-built features"** first → then
expanded to also require **detailed onboarding user guides for every section of the app,
updated after every major change** → then, after those were done, asked to also fix a
previously-known **CMS autosave bug** → then asked "what other items need work," was shown a
punch list pulled from the guides' own "Known limitations" sections, and picked:
1. **4 quick, low-risk fixes** — approved to just do them all.
2. **3 bigger scoped items** — approved to tackle next: multi-site CMS admin UI, CJ Ads button +
   campaign unsubscribe UX, SLA-breach automation trigger.

## Fully done and verified this session (already landed — confirm still true before assuming)

These were built, tested (full 1465-test suite + `verify-release.sh` all green each time), and
appear to already be committed (they don't show in current `git status`, see "Current working
tree state" below) — re-verify current git log if picking this up cold, but treat as done unless
you find otherwise:

- **Support Ticket System, all 5 phases**: notifications (pre-existing), attachments
  (`SupportTicketAttachment`, `/support/attachments/{id}` forced-download endpoint), tags +
  canned responses, Automation triggers (`support.ticketCreatedTrigger`/
  `support.ticketRepliedTrigger`) + SLA target fields (`FirstResponseDueAt`/`ResolutionDueAt`,
  informational only, not yet trigger-driven — see bigger-items list below), CSAT rating.
- **CMS Canvas Studio Phase 5**: Native No-Code Interactions & Animation Engine —
  `WidgetInteraction` on `LayoutWidget` (`PageLayoutModels.cs`), rendered via
  `CmsBlockHtmlRenderer.WrapWithInteraction`/`BuildInteractionRuntimeScript`, wired into both
  public-page routes in `Program.cs`, keyframes in `wwwroot/cms-public.css`, Inspector UI in
  `CmsBuilderEditor.razor`.
- **9 new user guides + index**: `docs/USER_GUIDES.md` plus `docs/CONTENT_CREATION_USER_GUIDE.md`,
  `SITE_BUILDING_USER_GUIDE.md`, `CRM_USER_GUIDE.md`, `SUPPORT_USER_GUIDE.md`,
  `SENTINEL_USER_GUIDE.md`, `INTELLIGENCE_USER_GUIDE.md`, `AFFILIATE_OPERATIONS_USER_GUIDE.md`,
  `PLATFORM_OPERATIONS_USER_GUIDE.md`, `CLIENT_PORTAL_USER_GUIDE.md` (existing
  `AUTOMATION_USER_GUIDE.md` was the style template). **Standing rule going forward**: update the
  relevant guide as part of any change that alters user-facing behavior — see memory
  `feedback_keep_user_guides_current` and `reference_user_guides_doc_set`.
- **CMS autosave bug fix**: `CmsBuilderEditor.razor` used to silently wipe a page's
  Category/Tags/CanonicalUrl/custom-field values on every autosave (its `CmsPageEditorModel`
  never populated those full-replace fields). Fixed by capturing `_preservedCategoryName`/
  `_preservedTags`/`_preservedCanonicalUrl`/`_preservedPropertyValues` once in `LoadAsync` and
  including them in every `SaveAsync`. Memory: `feedback_canvas_studio_autosave_wipes_page_fields`
  (marked FIXED).

Relevant memory entries to read for full detail/rationale on all of the above:
`project_5_cms_features_progress`, `project_support_ticket_notifications`,
`reference_user_guides_doc_set`, `feedback_keep_user_guides_current`.

## Current working tree state (uncommitted, in progress right now)

```
M  docs/AFFILIATE_OPERATIONS_USER_GUIDE.md
M  docs/CRM_USER_GUIDE.md
M  src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/AffiliateAnalytics.razor
M  src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Billing.razor
M  src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/CmsBuilderEditor.razor  (unrelated — the autosave fix above, not yet committed either apparently)
M  src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/EmailCampaigns.razor
M  src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/NewsIntelligence.razor
M  src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Scheduling.razor
```

This is the **"4 quick fixes" batch**, status per item:

1. **Affiliate Analytics mislabeling — DONE, code + guide.** Relabeled "Total Commission
   (all-time)" → "Total Commission (last 90 days)" and "Total Clicks" → "Total Clicks (last 90
   days)" in `AffiliateAnalytics.razor` (confirmed via `AffiliateAnalyticsService.cs`'s
   `DashboardWindow = TimeSpan.FromDays(90)`). `docs/AFFILIATE_OPERATIONS_USER_GUIDE.md` updated
   to match (both the body text and the "Known limitations" bullet were rewritten, no longer
   calls the label "misleading" since it's now accurate).

2. **Missing confirmation dialogs — DONE, code done, guide done.** Added
   `await JS.InvokeAsync<bool>("confirm", "...")` (same pattern as `Comments.razor`) before:
   - `Billing.razor`'s `DeleteDraftAsync` (added `@inject IJSRuntime JS`)
   - `Scheduling.razor`'s `DeleteAsync` (booking type — message explicitly warns it cascades) and
     `CancelAsync` (booking) (added `@inject IJSRuntime JS`)
   - `EmailCampaigns.razor`'s `DeleteAsync` (campaign) (added `@inject IJSRuntime JS`)
   `docs/CRM_USER_GUIDE.md` updated in **5 places**: 3 body-text mentions rewritten from "no
   confirmation prompt" to "confirmation required", plus the "Deleting a booking type cascades
   silently" Known-limitations bullet reworded (cascade behavior is still real, just no longer
   silent), plus the whole "no confirmation prompt" Known-limitations bullet **deleted** since
   it's now fully resolved. Build passed clean (`dotnet build src/GwsBusinessSuite.Web`).

3. **Media Watch disable toggle — DONE, code done, guide NOT YET UPDATED (stopped here).** Added
   a "Active (refreshes on schedule)" checkbox to the topic edit form in `NewsIntelligence.razor`
   (only shown when editing an existing topic — a new topic always starts active), backed by new
   `_formIsActive` field, wired through `OpenAddForm`/`OpenEditForm`/`SaveTopicAsync` (previously
   `SaveTopicAsync` always passed the topic's *existing* `IsActive` value straight back to
   `UpdateTopicAsync`, silently ignoring the fact that the UI had no way to actually change it).
   Build passed clean. **`docs/INTELLIGENCE_USER_GUIDE.md` still needs updating** — grep found 3
   spots that need editing (ran `grep -n "deactivate\|strikethrough\|disabled state"
   docs/INTELLIGENCE_USER_GUIDE.md` and got matches at lines ~56, ~98, ~254 before being cut
   off — re-run that grep, the line numbers may have shifted slightly since other edits landed).
   The "Known limitations" bullet at (former) line 254-255 ("No way to deactivate a Media Watch
   topic from the UI...") needs to be removed or rewritten to reflect that this is now fixed,
   same treatment as the CRM guide edits in item 2 above. The body-text mention around line 98
   ("...there's currently no way to deactivate a topic from the UI (see...)") also needs
   rewriting — check what section that sentence is in and update it to describe the new toggle.

4. **Media Library filename dedup — NOT STARTED.** This is the 4th quick fix the user approved
   ("warn or replace on duplicate filename upload"). `MediaLibraryService.UploadAsync`
   (`src/GwsBusinessSuite.Application/CmsBuilder/MediaLibraryService.cs`) currently always
   inserts a new `MediaAsset` row regardless of whether a file with the same `FileName` already
   exists — see memory `feedback_media_library_no_filename_dedup` for full context. **Needs a
   product decision baked into the fix**: does "warn" mean a confirmation dialog before upload
   (`Media.razor`'s upload handler), or does "replace" mean silently overwriting the existing
   asset's content in place (which would also require deciding whether to keep the old asset's
   `Id` so existing page references stay valid — probably yes, that's the whole point). Given the
   user said "warn or replace" (an either/or, not a firm decision), the safest default matching
   this app's existing UX conventions (confirm dialogs before destructive actions) is: on
   upload, check for an existing `MediaAsset` with the same `FileName` in
   `MediaLibraryService.UploadAsync`'s caller (`Media.razor`'s upload handler) *before* calling
   `UploadAsync`, and if one exists, show a `confirm()` dialog ("A file named 'X' already exists.
   Replace it?") — on confirm, either (a) add a new
   `IMediaLibraryService.ReplaceAsync(existingAssetId, fileName, content, altText, ct)` method
   that overwrites the existing row's `DataUri`/`ThumbnailDataUri`/`ContentType`/`SizeBytes` in
   place (preserving `Id` so existing references keep working), or (b) just call the existing
   `UploadAsync` after deleting the old one first — (a) is safer since it means "replace" doesn't
   temporarily leave zero assets with that filename if something fails mid-way, and doesn't churn
   the asset's `Id`. On cancel, either abort the upload or fall back to today's behavior
   (duplicate insert) — recommend aborting, since silently creating a duplicate after the user
   explicitly saw and dismissed a warning defeats the point of asking. This needs its own
   `MediaLibraryServiceTests.cs` coverage (new or extended) plus a `docs/SITE_BUILDING_USER_GUIDE.md`
   update (its Media Library section + Known-limitations bullet both currently describe the
   duplicate-creates-a-second-asset behavior and need rewriting once fixed).

## Next after the quick-fix batch: the 3 bigger approved items (not started)

The user explicitly approved these three (out of a longer list) as what to tackle next. Each is
a real, scoped feature — treat as its own short design pass before coding, matching this
session's established convention (see `AUTOMATION_USER_GUIDE.md`'s own delivery history for the
pattern: read the actual current code first, don't assume the guide's description is
still accurate by the time you get to it, verify against source).

1. **Multi-site CMS admin UI.** Currently every CMS admin screen (`Pages.razor`,
   `CmsBuilderEditor.razor`, `AppearanceCustomize.razor`, `AppearanceMenus.razor`, `Media.razor`,
   `Settings.razor`) is hardcoded to one `CmsSite` via a `Canvas:SiteSlug`-style config value,
   despite the backend model fully supporting multiple `CmsSite` rows (confirmed: "Import
   recipe" in Site Building deliberately creates a brand-new `CmsSite`). Needs: a site
   switcher (probably in the admin nav or a dedicated `/admin/sites` list page), every one of
   those 6 screens updated to operate on a selected/current site rather than a config-resolved
   one, and likely a `CurrentCmsSiteAccessor`-style scoped service (check if something like this
   already exists for other "current X" patterns in this app, e.g. `CurrentUserAccessor`, before
   inventing a new pattern). This is the largest of the three — do a proper design/plan pass
   before touching code, and confirm scope with the user (does "site switcher" mean a dropdown
   that changes what all 6 screens show, a full separate settings area per site, etc.) if it's
   ambiguous once you're looking at the actual current single-site assumptions baked into each
   page.

2. **CJ Ads button + campaign unsubscribe UX.** Two related-but-separate fixes:
   - `CjAds.razor`'s "Choose Ad" button currently just opens the raw CJ tracking URL in a new
     tab — it doesn't create an actual article placement (that only happens via the Article
     Editor's Placements panel, manually, or via applying an Affiliate Suggestion). The fix here
     is either relabeling the button to be honest about what it does ("Preview" or "Copy Link"
     instead of "Choose Ad"), or actually wiring it to create a placement — confirm which with
     the user, since "wire it up" is a bigger scope change than "fix the label."
   - Email Campaigns: unsubscribe is a global flag (suppresses a contact from *every* campaign),
     with no admin-side "resubscribe" control anywhere in `EmailCampaigns.razor`/
     `IEmailCampaignService`. Needs a resubscribe action (button on the contact/campaign detail
     view, clearing whatever the unsubscribe flag actually is — check `Contact`/`CmsSite` entity
     for the exact field name before implementing).

3. **SLA-breach automation trigger.** `SupportTicket.FirstResponseDueAt`/`ResolutionDueAt` exist
   and are surfaced as red-when-overdue text + a badge, but nothing fires an automation when a
   ticket actually breaches. Needs: a background sweep (mirror
   `AutomationTriggerService`/existing background-service patterns like
   `GrowthReportBackgroundService`'s scope-per-tick shape) that periodically checks
   Open/Pending tickets for `FirstResponseDueAt`/`ResolutionDueAt` in the past, and a new
   trigger node type (e.g. `support.ticketSlaBreachedTrigger`) following the exact same wiring
   pattern already used for `support.ticketCreatedTrigger`/`support.ticketRepliedTrigger` this
   session (`AutomationNodeRegistry.cs`, `AutomationWorkflow.TriggerSupportTicketSlaBreached`
   bool flag, `AutomationExecutionModes.SupportTicketSlaBreached`,
   `AutomationTriggerService.TriggerSupportTicketSlaBreachedAsync`, mode→trigger mapping in
   `AutomationExecutionService`). Needs a decision on whether a breach fires once (needs a
   "already notified" flag on the ticket, e.g. `FirstResponseBreachNotifiedAt`/
   `ResolutionBreachNotifiedAt`, to avoid re-firing every sweep tick) or repeatedly — recommend
   once, matching how most SLA tools behave, but confirm if it matters to the user.

## Verification checklist for whoever picks this up

- `dotnet build GwsBusinessSuite.slnx` — must be 0 warnings, 0 errors (this repo's standard).
- `dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj` — full suite was at
  1465 passing as of this handoff; should only grow, never shrink or fail.
- `./scripts/verify-release.sh` before ending any turn that changed code (CLAUDE.md requirement)
  — expect "PARTIAL" overall (the "Deployed *" checks are NOT RUN without a live URL, that's
  normal and not a failure) but every local check (Restore, Dependency audit, Release build, Full
  automated suite, Docker Compose rendering, Patch whitespace) must PASS.
- Any UI-only change (like items 3-4 above and all three bigger items) can't be click-through
  verified in a real browser — mandatory MFA on every admin login blocks scripted browser
  automation without a live TOTP secret (see memory `project_mandatory_mfa_blocks_scripted_login`).
  Verify via build + tests + careful code reading instead, and say plainly in any summary that
  live UI click-through wasn't done, rather than claiming full verification.
- Per the standing instruction (`feedback_keep_user_guides_current`), update the relevant
  `docs/*_USER_GUIDE.md` as part of each fix, not as separate follow-up work.
