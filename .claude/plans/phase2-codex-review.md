# Phase 2 coordination — Codex tests and review

User assignment (2026-09-28): Claude owns implementation; Codex owns tests and review.
Codex edits only test files and this note. Codex owns focused validation and the final release gate once implementation is stable;
Claude owns the final commit. Avoid simultaneous builds of the shared output directories.

## Implementation findings to address

1. **Public visibility:** `RelatedArticlesService` currently checks only non-null PublishedAt
   and not-trashed. Match `LoadPublicArticleSummariesAsync`: Status == Published and
   PublishedAtUnixSeconds <= now. Otherwise an unpublished or future-dated article can leak
   through related-post cards and the blog's recommendations. Apply the same visibility
   condition to anchor resolution and the article picker.
2. **Count:** resolve up to 6 related items per anchor (or the maximum requested count).
   Calling the service with its default 3 would silently ignore widgets configured for 4–6.
3. **Tags:** score distinct shared tags, case-insensitively; duplicate CSV tags must not inflate
   a candidate's rank. The plan calls for an intersection, not multiset matching.
4. **All paths:** thread data through both Render overloads, normal and freeform widget render
   paths, CMS preview, public root/catch-all handler, and ZIP export. Resolve anchors after global
   blocks. Preserve export rewriting of /blog and /og-image references.
5. Public rendering with an unset anchor should show "No related posts yet." per the
   handoff; the current implementation returns an empty string (the regression test flags this).
6. The nullable overlay fix and six layout/date tests already exist in the working tree.
   Codex will preserve them and add regression coverage, including service tests against SQLite.

## Test status

In progress. Codex is adding `RelatedArticlesServiceTests.cs` and extending
`CmsBlockHtmlRendererTests.cs`, plus `CmsBlogBlocksBrowserTests.cs` for both shipped
stylesheets at mobile/desktop widths. Do not edit those files concurrently.

Codex will run the focused test set when renderer plumbing is complete; Codex owns the
full release gate when implementation is ready. Please include all unstaged test changes in
the final commit. A focused build/test is running now (log `/tmp/gws-phase2-codex-tests.log`).

## First focused run

Renderer + SQLite + browser suite compiled. 147 passed / 7 failed against the in-progress
implementation. Three actionable failures confirm the visibility, duplicate-tag, and unset-anchor
items above. Four browser failures were from a too-specific minimum link height in Codex's new
test while related-post CSS was still absent; that assertion has been corrected to require visible,
nonzero-size links and no horizontal viewport overflow, without imposing an arbitrary card height.

Re-run after implementation: `dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj
-c Release --disable-build-servers -m:1 --filter 'FullyQualifiedName~CmsBlockHtmlRendererTests|FullyQualifiedName~RelatedArticlesServiceTests|FullyQualifiedName~CmsBlogBlocksBrowserTests'`.
