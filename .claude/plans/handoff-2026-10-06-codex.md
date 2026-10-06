# Handoff to Codex - 2026-10-06

Claude is out of weekly budget. This file is the full state; read it before touching anything.

## Rules for this repo (from CLAUDE.md - binding)

- **Never push.** Grant commits and pushes himself. A push to `origin/main` deploys to production.
- Before ending any turn that changed code, run `./scripts/verify-release.sh` (build + full test
  suite + Playwright). Treat every working-tree edit as able to ship at any moment.
- Update the matching `docs/*_USER_GUIDE.md` with every feature change.
- Finish a multi-part unit and verify it before stopping; don't leave it half-applied.

## Environment gotchas (cost Claude real time)

- Non-interactive shells here have no Homebrew on PATH: `export PATH=/opt/homebrew/bin:$HOME/.dotnet/tools:$PATH`
  first, or `dotnet` / `dotnet-ef` silently "succeed" by not running at all.
- New migration: `./scripts/add-migration.sh <Name>` (needs dotnet-ef from `~/.dotnet/tools`).
- Local repro without touching the dev DB: run the Web project in Development with
  `ConnectionStrings__DefaultConnection="Data Source=<scratch>/diag.db"`, `AdminAuth__Username=diagadmin`,
  `AdminAuth__Password=<a strong password>`, `ASPNETCORE_URLS=http://127.0.0.1:5399`, then drive it with
  Playwright (Chromium is at `~/Library/Caches/ms-playwright/chromium-1234`). Local dev skips MFA.
  A test app may still be listening on port 5399 - `lsof -ti tcp:5399 | xargs kill` first.
- Builds/tests are slow (~2-12 min for big filters). The full suite in verify-release took ~140s last time.

## 1. Working tree right now: Media Watch work, UNCOMMITTED and NOT fully verified

Everything below is in the working tree (`git status`), not committed. `verify-release.sh` has NOT
been run on it.

Built (plan: `.claude/plans/intelligence-expansion-2026-10.md`, "Media Watch" status block):
merge-on-refresh (only new articles get AI takes), per-admin read/unread, Saved + retention setting
(1/2/3/7 days), story grouping across outlets, per-topic refresh issues, new "Breaking" rule (3+
outlets in 6h), daily/weekly digest email, `news.articlesFoundTrigger` automation trigger, Clip to
Sentinel, Trends view, "Find a site's feed" in the topic form.

Key files: `Application/NewsIntelligence/{NewsStories.cs,INewsWatchService.cs}`,
`Infrastructure/Services/{NewsWatchService.cs,NewsIntelligenceService.cs,NewsRefreshBackgroundService.cs}`,
`Domain/Entities/MediaWatchEntities.cs`, migration `20261006172044_AddMediaWatchReadingStateAndTrends`,
`Web/.../NewsIntelligence.razor(.css)`, `tests/.../MediaWatchTests.cs`, guide `docs/INTELLIGENCE_USER_GUIDE.md`.

Verified so far: MediaWatchTests + ThreatExposureTests 56/56 pass; NewsIntelligence + Automation
suites passed (252/254 before two fixes that then passed). Live browser run confirmed story
grouping, read marking (badge 25 -> 24), clip to Sentinel, feed discovery ("Ars Technica (20 items)").

**Known bug, fix not yet applied (Grant interrupted the edit):** in `NewsIntelligence.razor`, the
"Trusted RSS feed URLs" `<textarea>` renders its text as child content (`>@_formTrustedFeeds</textarea>`),
so clicking **Add** on a discovered feed updates the field but the box doesn't show it. Fix: use
`value="@_formTrustedFeeds"` on the textarea with empty content. Then re-run the walkthrough.

Still to do for Media Watch: apply that fix, check the Trends and Digest & settings views in a
browser (settings save + "Send a digest now"), run the responsive audit on
`/admin/news-intelligence` (`GWS_AUDIT_ROUTES=/admin/news-intelligence` + `GWS_AUDIT_*` vars, see
`tests/.../ResponsiveAuditTests.cs`), then `verify-release.sh`.

Also uncommitted: `ThreatIntelligence.razor.css` dark-mode masthead fix (verified in both themes),
and email validation tightened in `ThreatMonitorService.SaveSettingsAsync` (MimeKit accepts a bare
"bob" as an address; both services now use `NewsWatchService.IsEmailAddress`).

## 2. Grant's new request (priority - he interrupted Media Watch for this)

Sentinel (`src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Wiki.razor`):

1. **Trash: select all + permanent delete.** Today items are deleted one by one. Add select-all
   (and per-item checkboxes if missing), a "Delete permanently" action, and a confirmation modal
   with **Delete** and **Cancel**. The page already uses `<ConfirmModal ...>` for bulk trash
   (search "Move {_selectedTreeNodeIds.Count} selected item(s)") - reuse it. Check what permanent
   delete API exists in `IWikiService` / `WikiDatabaseService` (search "PermanentlyDelete" / "Purge").
   Remember trashed pages can have trashed descendants and databases.
2. **Drag a Recent page onto a parent page to make it that page's child.** The sidebar "Recent"
   list is rendered near the top of Wiki.razor (`Navigation.Recent...`, `sentinel-nav-item`).
   Tree drag-and-drop/reorder already exists (`wwwroot/js/dragReorder.js`,
   `WikiService.ReorderPageAsync`, `MoveToSelectedParentAsync`, the "move under" bulk action) -
   reuse that move path; don't build a second one. Rules from Grant: one parent can have many
   children; a child has exactly one parent. Moving under its own descendant must be refused
   (existing code already guards this in bulk move - reuse).
3. **`/page` still "opens a blank page but doesn't connect it to the parent".** Facts so far:
   - The fix (commit `586c9db`, deployed 2026-10-06 01:25 UTC, deploy succeeded) makes `/page`
     create the child with `ParentWikiPageId`, replace the typed `/page` block with a page-link
     card, save the parent, then open the child with the title focused. Flow:
     `wiki-block-editor.js` `createChildFromPicker` -> `CreateChildPageFromEditor` ->
     `OpenCreatedChildFromEditor` (Wiki.razor ~1755-1800).
   - Production serves the new script (checked: `/js/wiki-block-editor.js?v=10` contains
     `createChildFromPicker`, `cache-control: max-age=14400`).
   - It worked in a local repro on a NEW parent page. The repro Claude was about to run
     (`.../scratchpad/child.js`, not run) covers Grant's real case: an EXISTING titled parent with
     content, opened fresh from the sidebar, then `/page` + Enter, then back to the parent.
   - Suspects, in order: (a) **the import is pinned to `?v=10` and was never bumped** when the
     script changed - the Mac app's WKWebView keeps its own long-lived cache (a past incident is
     documented in Program.cs's CSP comments), so it may still run the old script. Bump both
     imports (`Wiki.razor` ~2161 and `SentinelDatabaseRowPage.razor` ~313) to `?v=11` regardless.
     (b) `PerformAutosaveAsync` starts with `if (IsBusy) return;` - if anything is busy when
     `OpenCreatedChildFromEditor` flushes, the parent's new link block is silently not saved
     before `OpenPageAsync` replaces the editor. (c) `CreateChildFromEditorAsync` returns null
     with "Save the parent page before creating a child." when the flush leaves unsaved changes
     (e.g. a version conflict) - then nothing happens in the UI.
   - Ask Grant whether he sees this in a browser or the Mac app, and whether the child shows
     nested under the parent in the sidebar tree.

## Other open items (not started)

- Intelligence expansion: Civic Watch, BI Dashboards and Overwatch's 10 items each - Grant hasn't
  picked an order. Plan file above.
- Overwatch camera coverage (`.claude/plans/overwatch-coverage-2026-10.md`): Singapore, Hong Kong,
  Iceland, MN/NE plow cams, Tennessee are verified leads, not built.
