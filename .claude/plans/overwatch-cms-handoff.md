# Overwatch + CMS block library — handoff for Codex

> **~~SUPERSEDED~~ (audit 2026-10-07):** Workstreams B-E are built (master plan resume status 2026-10-03). Only Overwatch coverage is open; see overwatch-coverage-2026-10.md and intelligence-expansion-2026-10.md.

**Status as of 2026-09-28.** This is a point-in-time handoff, not the master plan — for full
architecture, market research, and the complete 5-workstream scope (Overwatch global coverage,
blog blocks, marketing blocks, 15 themes, community/intranet system), read
`.claude/plans/humming-mapping-hartmanis.md` first. This file exists only to say precisely what's
committed, what's mid-flight right now, and what to pick up next, so nothing gets redone or
stepped on.

## Repo state right now

- `main` is at commit `6742b77` ("Refactor camera coverage regions and incident providers") —
  this includes the full second Overwatch coverage batch (16 new camera providers, 6 new/shared
  incident providers, the Overwatch fullscreen toggle button, and several real fixes Codex made
  on top of the original batch — a Maryland camera provider removal, a DriveBC CSV
  culture-invariant parsing fix, and a MissouriDotIncidentProvider rewrite to use MoDOT's real
  incident layers instead of the original thinner schema). All of that is pushed to
  `origin/main`.
- **Uncommitted in the working tree right now** (Workstream B, Phase 2 — `posts-grid` layout
  variants + the new `related-posts` widget), **mid-flight, not yet fully verified**:
  - `src/GwsBusinessSuite.Application/CmsBuilder/CmsBlockHtmlRenderer.cs` — `posts-grid` now
    supports 4 layouts (`grid`/`list`/`classic`/`overlay`) via `RenderPostsGridDefault/List/
    Classic/Overlay` + a `PostDate` helper, plus a new `showDate` prop. **Has one known,
    unfixed nullable-reference warning**: line ~745, `Html(article.HeroImageUrl)` inside
    `RenderPostsGridOverlay` — `HeroImageUrl` is `string?` on `PublicArticleSummary` but the
    call site is already inside an `if (hasImage)` block, so the fix is either a `!` null-forgiving
    operator or an `is not null` pattern capture; `hasImage` already checked
    `!string.IsNullOrWhiteSpace(article.HeroImageUrl)` just above. **Fix this before doing
    anything else** — this repo has a zero-warnings standard.
  - `src/GwsBusinessSuite.Application/CmsBuilder/PublicSiteHtmlRenderer.cs` — `RelatedArticleView`
    gained a trailing `HeroImageUrl` field (additive, defaulted, existing call sites unaffected).
  - `src/GwsBusinessSuite.Application/CmsBuilder/RelatedArticlesService.cs` — **new file**,
    untracked. Extracted from `Program.cs`'s old `GetRelatedArticlesAsync` local function.
    Deliberately built in the **Application** layer using `IAppDbContext` (not a new Infrastructure
    service as an earlier draft of the master plan assumed) — `IAppDbContext` already exposes
    `DbSet<Article> Articles` with zero direct EF-provider dependency, matching the existing
    `CommentService` precedent exactly. Registered in DI as
    `services.AddScoped<IRelatedArticlesService, RelatedArticlesService>();`.
  - `src/GwsBusinessSuite.Web/Program.cs` — the `/blog/{slug}` route now takes
    `IRelatedArticlesService relatedArticlesService` as a handler parameter and calls
    `relatedArticlesService.GetRelatedArticlesAsync(a)` instead of the old local function (which
    has been deleted). **The 3 `CmsBlockHtmlRenderer.Render(...)` call sites (lines ~1918, ~2732,
    ~3379) have NOT yet been touched** — see "Not started" below.
  - `src/GwsBusinessSuite.Infrastructure/DependencyInjection.cs` — `IRelatedArticlesService`
    registration added.
  - `src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/CmsBuilderEditor.razor` —
    `posts-grid`'s `CreateDefaultWidget` entry and inspector panel both updated for the new
    `layout`/`showDate` props (a `Layout` select, a `Columns` select now conditionally shown only
    when `layout == "grid"`, a `showDate` checkbox). **No `related-posts` widget type has been
    added to the catalog/editor at all yet.**
  - `src/GwsBusinessSuite.Web/wwwroot/cms-public.css` and `public-site.css` — both got the new
    `.gws-posts-grid-list/-classic/-overlay` + `.gws-posts-grid-date` rules, each using that
    file's own existing token conventions. No new CSS for `related-posts` yet.
- **Last confirmed build**: `dotnet build src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj -c
  Release` succeeded with the one warning above (not yet re-verified after this note was
  written). **No tests have been written or run for anything in this uncommitted batch yet.**

## Immediate next steps, in order

1. **Fix the CS8604 warning** in `RenderPostsGridOverlay` (see above) — confirm zero warnings on
   a full rebuild of `GwsBusinessSuite.Web.csproj` afterward.
2. **Write tests for the `posts-grid` layout variants** in
   `tests/GwsBusinessSuite.Tests/CmsBlockHtmlRendererTests.cs`, matching that file's existing
   `Layout(widgetJson)` helper pattern (see the existing `posts-grid` tests already in that file
   for the exact idiom: construct a small `List<PublicArticleSummary>`, call
   `CmsBlockHtmlRenderer.Render(...)` with `articles:` passed explicitly, assert on wrapper
   classes/structural markers per layout). Confirm the **existing** `posts-grid` tests (default
   grid layout) still pass unmodified — they should, since `layout` defaults to `"grid"` and
   `RenderPostsGridDefault` is byte-for-byte the old implementation plus the new optional date
   span.
3. **Finish the `related-posts` widget** — this is the larger remaining piece of Phase 2, and
   essentially nothing has been built for it yet beyond the `RelatedArticlesService`/
   `RelatedArticleView` groundwork above. Per the master plan's Phase 2 spec (Workstream B):
   - New widget type `related-posts` in `CmsBlockHtmlRenderer.cs`: a `RenderWidget` case, a
     `PlainTextPreview` case, and a new private `RenderRelatedPosts` method.
   - Props: `sourceArticleSlug` (required — the article this block shows recommendations *for*,
     not the current page), `count` (default 3, clamp 1–6), `showImage` (default `true`).
   - `Render()`/`Render(string blocksJson, ...)` (both overloads, `CmsBlockHtmlRenderer.cs:72,75`)
     need a new trailing parameter:
     `IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>>? relatedPostsByAnchorSlug = null`.
   - Two new guard/helper methods mirroring `LayoutContainsPostsGrid`'s own shape: `static bool
     LayoutContainsRelatedPosts(PageLayout? layout)` and `static IReadOnlyList<string>
     GetRelatedPostsAnchorSlugs(PageLayout? layout)` (distinct non-empty `sourceArticleSlug`
     values across the whole page, since more than one `related-posts` block can point at
     different anchor articles).
   - All 3 `Program.cs` call sites (~1918, ~2732, ~3379): when `GetRelatedPostsAnchorSlugs(layout)`
     is non-empty, resolve each slug to a real `Article` (via `db.Articles` or the dbFactory,
     matching each call site's existing pattern), call
     `relatedArticlesService.GetRelatedArticlesAsync(article)` per anchor, build the
     `Dictionary<string, IReadOnlyList<RelatedArticleView>>`, and pass it into `Render(...)`.
     Each of these 3 call sites will need `IRelatedArticlesService` added to its own
     handler/method's parameter list, the same way the `/blog/{slug}` route already got it.
   - Editor: `CmsBuilderEditor.razor` — catalog entry (`GroupDynamic`, next to `posts-grid`),
     `CreateDefaultWidget` default props, `GetWidgetTypeLabel`/`GetWidgetIconClass`/
     `GetWidgetLayerSummary`, and an inspector block with a **dropdown of published articles** for
     `sourceArticleSlug` — populate it by injecting `IDbContextFactory<ApplicationDbContext>`
     directly into the component's `@code` block, matching `ArticleEditor.razor`/`Settings.razor`'s
     own established pattern for this exact kind of admin-picker dropdown.
   - Rendering: edit-mode placeholder text ("Pick a source article in the Inspector") when
     `sourceArticleSlug` is unset; public mode shows "No related posts yet." when the anchor slug
     isn't found or has zero score-matches (mirrors `RelatedArticlesService`'s own score-0
     filtering — an anchor with no real matches is a normal, expected outcome, not an error).
   - New CSS: `.gws-related-posts` / `.gws-related-posts-item` in both `cms-public.css` and
     `public-site.css`, following the same per-file token conventions as everything else in this
     batch.
   - Tests: extend `CmsBlockHtmlRendererTests.cs` (render with a supplied dictionary; empty-state
     when the anchor isn't in the dictionary; edit-mode placeholder; guard tests mirroring the
     existing `LayoutContainsPostsGrid` guard tests) and add
     `tests/GwsBusinessSuite.Tests/RelatedArticlesServiceTests.cs` (new, Infrastructure-style test
     using a real SQLite in-memory `ApplicationDbContext`, following `QuickNoteServiceTests.cs`'s
     precedent — test the scoring math directly: same-category +2, per-shared-tag +1,
     score-0 filtered out, ordering, and the `take` clamp to 1–6).
4. **Full verification before calling Phase 2 done**: `dotnet build` (zero warnings), full
   `dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj -c Release`, then
   `./scripts/verify-release.sh`. This repo's `CLAUDE.md` requires this before ending any turn
   that changed code, and separately requires finishing a whole multi-part phase (not stopping
   half-applied) before considering it done.
5. **Commit** — the user has not said whether to push automatically; ask, or match whatever
   pattern is currently in effect (recent commits on this branch were pushed directly, so a
   commit-then-push may be expected, but confirm rather than assume if unsure).

## Not started at all (do not attempt unless specifically asked)

- Workstream B, Phase 3 (newsletter signup — a `CmsSectionTemplates.cs` entry only, no new
  widget/renderer code) and Phase 4 (table-of-contents + reading-progress, both need the
  `BuildInteractionRuntimeScript()` third-call-site emission-gap fix in `Program.cs` (~3379) as a
  prerequisite — see the master plan for full detail on that pre-existing bug).
- Workstream A's remaining unresearched US states/countries (~40+ states were never covered by
  either research batch — see the master plan's Workstream A for the full confirmed/key-gated/
  dead-end breakdown so far).
- Workstreams C (marketing-page blocks), D (15 themes), E (community/intranet system) — all
  fully unstarted, fully specified in the master plan.
