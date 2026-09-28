# Master Plan: Overwatch Global Coverage + Full CMS Expansion

**Status: supersedes every prior plan/report on these topics.** Once approved, this file is
the single source of truth for all five workstreams below — the old blog-block-library-only
plan (this same file, previous revision), `reports/Marketing-theme module inspiration.md`, and
`reports/Oqtane CMS architecture research.md` are folded in here as reference material, not
separate active plans. Do not re-derive scope from them independently; they're cited inline
below wherever their findings feed a decision.

## Resume status (2026-09-27)

- Workstream B Phase 1 is committed as `1cc1bf6`; live inspector click-through remains outstanding.
- Workstream A's second batch is committed as `e0702b3`. This supersedes the handoff's
  uncommitted status; the user's single-batch decision supersedes A.2's per-region commit advice.
- Resume verification found the Missouri incident provider pointed at the camera layer and
  Maryland's player-page URLs were incorrectly treated as snapshots. Corrections use verified
  Missouri incident layers and exclude Maryland pending a compatible direct-media feed.
- DriveBC coordinate parsing must use invariant culture. The camera-only coverage count is
  20 US states after excluding Maryland; the larger coverage panel also includes incident regions.
- Workstream B Phases 2–4 remain next; C, D, and E have not started. Finish the Overwatch
  corrections and release gate before beginning the next CMS phase.

## Context

This consolidates every unresolved workstream identified in a 2026-09-27 status review into one
plan, per explicit instruction: build "a brand new plan of action to finish these all," with
market research and references, for approval before any execution resumes. Five substantial
workstreams are open:

- **A. Overwatch nationwide/worldwide camera + incident coverage.** A 2026-09-26 batch added 8
  new zero-key camera sources (FL/IL/IA/CA/OR/NYC/NZ/QLD) and 3 incident sources (QLD/MN/NE) on
  top of pre-existing GA/WA/Datumfeed/KartaView coverage. The user's actual ask — "every state...
  and the globe" — was undershot; ~42 US states and most countries were never researched at all.
- **B. Blog/Content Block Library** for the page editor (Foxiz-inspired). Fully designed in the
  prior revision of this plan file (research already done, phases fully scoped) but **zero code
  written** — confirmed via a fresh grep of `CmsBlockHtmlRenderer.cs` for `author-box`/`callout`/
  `related-posts`/`table-of-contents`/`reading-progress`, all absent.
- **C. Marketing-page block library** (9-theme ThemeForest research already done, saved at
  `reports/Marketing-theme module inspiration.md`). No implementation plan existed yet — written
  fresh below, in workstream C.
- **D. 15 unique visual themes.** Never planned in any detail beyond a one-line direction
  ("map onto the existing `DesignTokenSet` preset pattern") from `reports/Oqtane CMS architecture
  research.md`. Planned fully below.
- **E. BuddyPress-style community/intranet system** (Woffice-inspired). Never planned. Planned
  fully below, grounded in fresh market research on BuddyPress/BuddyBoss/Woffice plus a real
  inventory of which internal primitives (Wiki, Comments, AppUser) already exist to build on.

Two minor housekeeping items are tracked but out of scope for active engineering work: a handful
of failing Dependabot dependency-bump PRs (routine, non-blocking, handle opportunistically), and
a live `sentinel:read` end-to-end check that is the user's own action item, not this plan's.

## Recommended sequencing

Workstream **A (Overwatch)** and **B (Blog blocks)** are architecturally unrelated (Application
C# vs. Blazor page-editor) and should run **in parallel**, matching the precedent already set
earlier in this project ("build both in parallel"). Within the CMS side, **C depends on nothing
B doesn't already establish** (same renderer/registration mechanism) and should follow
immediately after B. **D (themes)** should follow C, not precede it — a curated set of 15
"starter site" presets is far more compelling once there's a full block palette (blog + marketing
blocks) to compose them from, and D's own architecture explicitly reuses CmsSectionTemplates,
which C extends. **E (community system)** is the largest, most novel, highest-schema-risk
workstream (new entities, new auth-adjacent surface, no existing UI to extend) and should come
last, once the team has full context on the page-editor/CMS patterns from B/C/D.

```
A (Overwatch)  ─────────────────────────────────────────────▶  done independently, any time
B (Blog blocks) ──▶ C (Marketing blocks) ──▶ D (15 themes) ──▶ E (Community/intranet)
```

---

## Workstream A — Overwatch nationwide + worldwide coverage

### A.0 — Research already banked this session (real, verified leads — use these first)

Direct web research (2026-09-27) surfaced concrete, higher-confidence candidates beyond the 8
already shipped:

- **CARS511 may cover more than MN/NE.** The `Cars511IncidentProvider` built in the last batch
  already hits `services.arcgis.com/8lRhdTsQyJpO52F1/` (an Iowa-DOT-owned ArcGIS org) for
  Minnesota and Nebraska *incident* layers only. That same org is worth querying for (a) a
  **camera** layer for MN/NE, and (b) whether **other states** publish through the same shared
  CARS511 hub — this is a "check one org, potentially unlock N states" lead, the highest-leverage
  item in this workstream. [Source: this session's own prior implementation of
  `Cars511IncidentProvider`.]
- **UK National Highways "Transport Data Feeds"** publishes ~2,700 public traffic cameras
  covering all English/Scottish/Welsh/Northern-Irish motorways and major A-roads (~2,000 England,
  300 Scotland, 250 Wales, 140 NI) — a real, documented open feed, not just London (which is
  already covered via Datumfeed's JamCams subset). [Source:
  [National Highways: Traffic camera/CCTV services](https://nationalhighways.co.uk/roads-and-travel/live-travel-updates/traffic-cameracctv-services/)]
- **DriveBC (British Columbia, Canada)** released open camera data in 2016: 320+ cameras with
  real GPS coordinates, view orientation, and direct image links — a real BC government open-data
  release, not a scrape. [Source:
  [BC Gov: Province opens up highway camera data](https://news.gov.bc.ca/releases/2016TRAN0045-000360)]
- **Ontario has an official 511 Developer API** distinct from whatever subset Datumfeed currently
  aggregates — worth checking whether it's a richer/more complete source than the existing
  Datumfeed-routed Ontario coverage. [Source: WebSearch result citing the Ontario 511 Developer
  API; needs direct verification, not yet fetched.]
- **A community-maintained aggregator registry exists**: `github.com/bzsasson/traffic-camera-sources`
  keeps a `data/sources.json` registry of official state DOT/511 feed sources plus a
  `cameras.geojson`. A live fetch of its raw JSON returned entries for **Alaska (511.alaska.gov),
  Arizona (az511.gov), Nevada (nvroads.com), Utah (udottraffic.utah.gov), Wisconsin (511wi.gov)**,
  though without endpoint-level detail (the summary tool could not surface exact field-level
  content from the JSON — the research phase below must fetch and read this file directly, not
  rely on this summary). [Source:
  [github.com/bzsasson/traffic-camera-sources](https://github.com/bzsasson/traffic-camera-sources)]
- **A third-party aggregator, road511.com**, claims to have "normalized 30 different 511 traffic
  APIs into one REST endpoint" — its own engineering writeup could be a shortcut map of exactly
  which 30 states/provinces have *some* programmatically-accessible feed, even if road511.com's
  own commercial API isn't used directly. [Source: WebSearch result; the specific dev.to article
  URL returned 404 on fetch and needs re-finding, not re-guessing, during Phase A.1.]
- General landscape confirmation: "roughly twenty jurisdictions share one vendor platform [for
  511 systems] while the rest run their own" — meaning a single vendor-platform reverse-engineer
  could unlock multiple states at once, similar to the CARS511 pattern already exploited for
  MN/NE. [Source: WebSearch synthesis citing multiple 511-system sources.]

### A.1 — Research phase (parallel background agents, verify-live discipline)

Every finding in this workstream must be **independently verified live** before being trusted —
this project's own established discipline (curl/WebFetch the actual endpoint, read real field
names/casing/pagination limits) caught a real, would-have-shipped bug last batch (NYC DOT's
camelCase JSON fields with no `[JsonPropertyName]` attributes, confirmed via live curl before
the fix). Never build a provider off a WebSearch summary alone.

Launch parallel research agents (`general-purpose`, background) once this plan is approved,
each with the mandate: **for every jurisdiction in its list, determine and report** (a) does a
genuinely free, non-authenticated, live camera and/or incident feed exist; (b) if yes — the exact
endpoint URL, response format (ArcGIS FeatureServer / plain JSON / GeoJSON), field names with
their real casing (confirmed via a live fetch, not docs), pagination limits, and spatial
reference; (c) if the only option needs a self-registered API key — note it and stop (do not
register a key on the user's behalf); (d) if genuinely no public feed exists — say so and move on.
Batches:

1. **Follow-up on the CARS511 lead** — one focused agent, not a whole region: query
   `services.arcgis.com/8lRhdTsQyJpO52F1/arcgis/rest/services` for its full service catalog (not
   just the two layers already known), identify every state/layer it hosts, and confirm whether a
   camera layer exists alongside the incident layers.
2. **Fetch and read `bzsasson/traffic-camera-sources`' actual `data/sources.json` and
   `data/cameras.geojson`** directly (not via a summarized WebFetch) and re-verify every listed
   endpoint live — treat this registry as a lead generator, not a source of truth.
3. **US Northeast + Mid-Atlantic**: CT, DE, ME, MD, MA, NH, NJ, NY (statewide, not just NYC), PA,
   RI, VT. (MD/MA/PA were flagged dead ends in an earlier research pass this project already ran —
   re-verify rather than re-trust, since APIs change and that research had its own noted gaps.)
4. **US Southeast**: AL, AR, KY, LA, MS, NC, SC, TN, VA, WV.
5. **US Midwest**: IN, KS, MI, MO (full state — only a Springfield-area subset was previously
   confirmed), ND, OH (previously flagged key-gated — re-verify), SD, WI (previously flagged
   key-gated — re-verify given the fresh `511wi.gov` lead above).
6. **US West/Mountain + territories**: AK (fresh `511.alaska.gov` lead), AZ (previously flagged
   key-gated — re-verify given the fresh `az511.gov` lead), CO, HI, ID, MT, NV (fresh
   `nvroads.com` lead), NM, OK, TX (full state — only Austin is currently covered, via
   Datumfeed), UT (fresh `udottraffic.utah.gov` lead), WY, plus Puerto Rico/Guam/DC if any public
   feed exists.
7. **International, round 2**: full UK (National Highways feed above — England/Scotland/Wales/NI,
   not just London), British Columbia (DriveBC), Ontario's official 511 Developer API (verify
   against/replacing the current Datumfeed-routed Ontario coverage if better), other Canadian
   provinces (Alberta, Quebec), other Australian states (NSW — previously flagged key-gated,
   re-verify; Victoria — previously flagged dead end, re-verify), Ireland, and a broad sweep of
   remaining major countries not yet checked (Germany/France/Italy/Japan/Netherlands were
   confirmed genuine dead ends previously — do not re-spend budget there without a new lead;
   Singapore/South Korea/India/Brazil/Mexico/Spain/Nordic countries are unresearched).

Each agent reports back a structured list (jurisdiction → verdict → endpoint detail or reason for
exclusion). No building starts until a jurisdiction's endpoint has been confirmed live.

### A.2 — Build phase (per confirmed jurisdiction, repeats the proven pattern)

For every jurisdiction A.1 confirms as genuinely free/no-key, follow the exact pattern already
used for the 8 sources shipped 2026-09-26:

1. New provider class in `src/GwsBusinessSuite.Application/CameraIntel/` (implements
   `ICameraFeedProvider`) or `.../TrafficIncidents/` (implements `ITrafficIncidentProvider`),
   following `OregonDotCameraProvider.cs`/`QueenslandTrafficIncidentProvider.cs` as the reference
   shape: private nested JSON records with **explicit `[JsonPropertyName]` on every field**
   (mandatory — this is exactly the bug class caught in NYC DOT's provider last batch), an
   `IMemoryCache`-backed "fetch once, filter by bbox per call" pattern, `try/catch` returning `[]`
   on `HttpRequestException`/`JsonException`/`TaskCanceledException`, `outSR=4326` on every
   ArcGIS query, and `resultOffset`/`resultRecordCount` pagination for any layer whose real record
   count exceeds its `maxRecordCount`.
2. DI registration in `src/GwsBusinessSuite.Infrastructure/DependencyInjection.cs`: **always**
   register each concrete provider type against itself
   (`services.AddHttpClient<TImplementation>(...)`) then bridge to the shared interface
   separately (`services.AddScoped<ICameraFeedProvider>(sp => sp.GetRequiredService<TImplementation>())`)
   — never `AddHttpClient<TInterface, TImplementation>(...)`, which additively shares one named
   `HttpClient` across every provider registered against that same interface and silently lets
   the last-registered one's `BaseAddress` win for all of them (this exact bug was found and
   fixed twice already, once for cameras, once for `GdotTrafficIncidentProvider`).
3. Unit tests in `tests/GwsBusinessSuite.Tests/`, one file per provider, mirroring
   `OregonDotCameraProviderTests.cs`/`Cars511IncidentProviderTests.cs`: a `RecordingHandler :
   HttpMessageHandler`, realistic sample JSON shaped from the real live response captured during
   A.1, assertions on mapped fields, an empty-list assertion on a simulated failure, and a
   pagination test for any paginated source.
4. Update `CameraCoverageRegion.cs`'s `CameraCoverageRegions.All` (one new entry per statewide/
   national region) and its test (`CameraCoverageRegionsTests.cs`).
5. Update `OverwatchGrid.razor`'s banner text and footer attribution line, and
   `docs/OVERWATCH_USER_GUIDE.md`'s coverage lists (finding-no-cameras hint, known-limitations
   section) — this project's own "keep user guides current" standing convention.
6. Full solution build (zero warnings), full test suite, `./scripts/verify-release.sh`, before
   considering a batch of jurisdictions done — per this repo's own CLAUDE.md standing rule to
   finish and verify the whole unit before stopping.

Batch jurisdictions into reasonably sized commits (e.g., by region, matching A.1's batching)
rather than one single giant commit, so a problem with one state's endpoint doesn't block
shipping the others.

### A.3 — Explicit exclusions (documented, not silently dropped)

Maintain a running "excluded" list in `docs/OVERWATCH_USER_GUIDE.md`'s Known Limitations section
(the pattern already established): key-gated sources the app deliberately doesn't request a key
for on the user's behalf (Ohio, Arizona, Wisconsin, Singapore, NSW Australia, pending A.1
re-verification), and genuine technical dead ends (Netherlands' Referer-gated feed; Germany/
France/Italy/Japan/Victoria-Australia/Pennsylvania/Maryland/Massachusetts, pending A.1
re-verification in case circumstances changed).

---

## Workstream B — Blog/Content Block Library (page editor)

*Carried forward verbatim from the prior revision of this plan file — fully designed, zero code
written. Re-stated here in full since this file is now the only source of truth.*

### Context

Extends the existing page editor ("Canvas Studio") with a library of drag-and-drop blog/content
blocks, inspired by the Foxiz newspaper/magazine WordPress theme. This is the smallest,
most self-contained of the CMS workstreams — it only extends the block palette, touching no
auth/community/theming systems.

**Critical scope boundary, confirmed directly from source**: the app's actual public blog pages
(`/blog` list, `/blog/{slug}` detail) are hardcoded HTML-string templates in
`PublicSiteHtmlRenderer.cs`, served via plain minimal-API routes in `Program.cs` — **not** built
from the page editor's `BlocksJson`. New blocks plug into ordinary `CmsPage`s an admin builds,
not automatically onto the existing `/blog/*` routes. Rewriting those routes onto the block
system is out of scope.

### Architecture recap (how a new block type is wired in)

A block "type" is a plain string discriminant (`LayoutWidget.WidgetType`). `PageLayout`
(`src/GwsBusinessSuite.Application/CmsBuilder/PageLayoutModels.cs`) is a 3-level tree: Sections →
Columns → Widgets. Every `LayoutWidget` shares one shape: `Props` (untyped
`Dictionary<string,string>`), `Style` (supports design-token references via `*Token` companion
fields — see Workstream D), `Visibility`, `EditPermission`, `Freeform`, `Interaction`,
`HiddenOnMobile/Tablet`.

Registering a new widget type touches, every time, for both this workstream and Workstream C:

1. `CmsBuilderEditor.razor` — `WidgetCatalog` array (~line 1608, insertion-palette registry),
   `CreateDefaultWidget` factory (~3675), `GetWidgetTypeLabel`/`GetWidgetIconClass`/
   `GetWidgetLayerSummary` (~3718+), and the inspector panel's `@if/else if
   (selWidget.WidgetType == "...")` chain (~515–885, bound via `GetProp`/`SetProp`).
2. `CmsBlockHtmlRenderer.cs` — `RenderWidget` switch (~505) + a private `RenderXxx` method (pure
   static HTML-string building — this one function serves the editor's live-preview iframe, the
   real public page, and the static-site-export feature) and `PlainTextPreview` switch (~45).
3. `GlobalBlockOverridableFields.cs` (optional) —
   `CandidatesByWidgetType: IReadOnlyDictionary<string,string[]>`.
4. CSS in **both** `wwwroot/cms-public.css` and `wwwroot/public-site.css` (hand-kept in sync).
5. `tests/GwsBusinessSuite.Tests/CmsBlockHtmlRendererTests.cs` — build a `LayoutWidget`/JSON
   snippet, call `Render(...)`, assert HTML substrings.
6. `Components/CmsBlockPreview.razor` (optional, best-effort — its own revision-diff preview
   switch already silently skips `posts-grid` today; only worth adding for cheap static widgets).

`Render(...)`'s confirmed current signature (`CmsBlockHtmlRenderer.cs:72,75`):
```csharp
public static string Render(PageLayout? layout, string siteSlug = "", string pageSlug = "",
    bool editMode = false, IReadOnlyList<PublicArticleSummary>? articles = null,
    bool isLoggedIn = false, DesignTokenSet? tokens = null)
```

**Confirmed real, pre-existing bug, must be fixed as a Phase 4 prerequisite**: `Program.cs` calls
`CmsBlockHtmlRenderer.Render(...)` at 3 call sites (lines ~1918, ~2732, ~3379) but calls
`BuildInteractionRuntimeScript()` after only 2 of them (~1949, ~2757) — the third (root-domain
public route) never emits it, silently breaking Phase-5 scroll/click animations there and would
break this workstream's new TOC/reading-progress scripts too.

`related-posts`' logic to extract and reuse: `GetRelatedArticlesAsync` local function in
`Program.cs` (~3544) — scores `+2` same `CategoryId`, `+1` per shared tag (case-insensitive CSV
intersection of `Article.Tags`), filters score-0, orders by score desc then
`PublishedAtUnixSeconds` desc, top 3.

`CmsSectionTemplates.cs` (confirmed shape, read directly this session):
`sealed record CmsSectionTemplate(string Key, string Icon, string Name, string Description,
Func<LayoutSection> Build)`, exposed via `CmsSectionTemplates.All`/`.Find(key)` — currently holds
6 entries (`feature-grid`, `testimonial-row`, `cta-banner`, `team-grid`, `faq`, `pricing-table`).
This is the mechanism for one-click pre-composed multi-widget sections, used by this workstream's
newsletter block and Workstream C's marketing sections alike.

### Design decisions (resolved)

1. **Post-grid layout variants → one widget type, a `layout` Prop**
   (`grid|list|classic|overlay`), not 4 separate types.
2. **Related-posts targeting → an explicit `sourceArticleSlug` Prop**, admin-picked from a
   dropdown of published articles.
3. **Author box → no new entity, static Props only** (name/avatar/bio/role/social links). A
   "real" author-profile system (new `Author` entity + FK + migration) is a separate,
   roughly-doubles-the-cost subsystem — not built here.
4. **Table of contents → client-side DOM scan**, not server-side sibling-widget awareness. Renders
   an empty `<nav data-gws-toc>` shell + one shared inline script that scans the rendered page's
   `h2`–`h4` at runtime, slugs/dedupes ids, builds the list, optionally scrollspies via
   `IntersectionObserver`.
5. **Reading progress bar → inline script**, following the Interaction Engine's
   `WrapWidget`/`BuildInteractionRuntimeScript` precedent. Needs `tokens` threaded one level
   deeper into `RenderWidget`'s internal dispatch — confirm exact plumbing during Phase 4.
6. **Newsletter signup → a `CmsSectionTemplates.cs` entry**, zero new renderer/widget code —
   pre-seeds a `form` widget (one `email`-typed, `role:"email"` field, `autoCreateContact =
   "true"`) styled as an accent-background section.
7. **Highlight + Note → collapsed into one widget, `callout`**, with a `variant` Prop
   (`info|success|warning|danger|note`).
8. **Accordion is already built** (`WidgetType == "accordion"`) — excluded from this plan.

### Phase 1 — Static content widgets (`author-box`, `callout`)

Lowest risk: no `Program.cs` changes, no renderer-signature changes.

**`author-box`** — Props: `name`, `roleOrTitle`, `bio` (markdown), `avatarUrl`, `websiteUrl`,
`twitterUrl`, `linkedinUrl`, `emailAddress` (each empty = hidden). `GroupMedia` catalog placement;
`GlobalBlockOverridableFields["author-box"] = ["name","bio","roleOrTitle","avatarUrl"]`; CSS
`.gws-author-box`/`-avatar`/`-social` in both stylesheets.
Tests: name/bio/avatar render; each social link hidden when empty, shown when set; plain-text
preview returns the name.

**`callout`** — Props: `variant` (`info|success|warning|danger|note`, default `info`), `title`
(optional), `body` (markdown, required), `showIcon` (default `true`). `GroupLayout` placement;
CSS variant classes in both stylesheets; `GlobalBlockOverridableFields["callout"] =
["title","body"]`.
Tests: one case per variant asserts CSS class; title omitted → no title element; icon toggle off;
markdown body renders through Markdig.

### Phase 2 — Live-data widgets (`posts-grid` variants, `related-posts`)

**`posts-grid` layout variants** — new Props: `layout` (`grid|list|classic|overlay`, default
`grid`), `showDate` (default `true`). `RenderPostsGrid` gains a layout branch; inspector adds a
`layout` select. No `Program.cs`/guard changes — reuses `LoadPublicArticleSummariesAsync`/
`LayoutContainsPostsGrid` as-is. CSS: `.gws-posts-grid-list/-classic/-overlay`.
Tests: one per layout variant; `showDate` on/off; existing `posts-grid` tests must still pass.

**`related-posts`** (new type):
- Extract `GetRelatedArticlesAsync` into `src/GwsBusinessSuite.Infrastructure/Services/
  RelatedArticlesService.cs` implementing `IRelatedArticlesService { Task<List<RelatedArticleView>>
  GetRelatedArticlesAsync(Article article, int take = 3) }`, `AddScoped` — required because
  `CmsBlockHtmlRenderer`/`Application` has no EF/Infrastructure reference. The `/blog/{slug}`
  route switches to calling this service instead of its local function.
- `RelatedArticleView` gets one additive field: `HeroImageUrl` as a trailing defaulted parameter.
- Props: `sourceArticleSlug` (required, dropdown of published articles — populate via
  `IDbContextFactory<ApplicationDbContext>` injected directly into `CmsBuilderEditor.razor`,
  matching `ArticleEditor.razor`/`Settings.razor`'s pattern), `count` (default 3, clamp 1–6),
  `showImage` (default `true`).
- `Render()` gains `IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>>?
  relatedPostsByAnchorSlug = null` (non-breaking).
- Guard: `LayoutContainsRelatedPosts(layout)` + `GetRelatedPostsAnchorSlugs(layout)` (distinct
  non-empty `sourceArticleSlug` values across the page — one query per distinct slug).
- All 3 `Program.cs` call sites: resolve each anchor slug to an `Article`, call the service,
  build the dictionary, pass into `Render(...)`.
- Edit-mode placeholder ("Pick a source article in the Inspector") when unset; public mode shows
  "No related posts yet." when the anchor isn't found or has no scored matches.

Tests: `RelatedArticlesServiceTests.cs` (new, Infrastructure, SQLite in-memory, following
`QuickNoteServiceTests.cs`'s precedent) — scoring math, score-0 filtering, ordering, `take` clamp.
`CmsBlockHtmlRendererTests.cs` — supplied-views render; empty-state; edit-mode placeholder; guard
tests mirroring existing `LayoutContainsPostsGrid` tests.

### Phase 3 — Newsletter signup (section-template composition)

Add `new CmsSectionTemplate("newsletter-signup", "📧", "Newsletter signup", "Email capture styled
as a callout", NewsletterSignup)` to `CmsSectionTemplates.All`. Factory builds an accent-background
`LayoutSection` with a heading, supporting copy, and a `form` widget pre-seeded with one
`email`-typed `role:"email"` field, `submitLabel = "Subscribe"`, `autoCreateContact = "true"`. No
new CSS for v1. No ESP/Mailchimp integration.

Tests: assert `CmsSectionTemplates.Find("newsletter-signup")` returns a section whose form widget
has the expected field/role/`autoCreateContact` seeded.

### Phase 4 — JS-requiring widgets (`table-of-contents`, `reading-progress`)

**Prerequisite**: fix the third-call-site script-emission gap in `Program.cs` (~3379) first —
required for this phase's scripts and Phase-5 animations to work identically on every render
path.

**`table-of-contents`** — Props: `title` (default "On This Page"), `minLevel`/`maxLevel`
(`h2`/`h3`/`h4`), `showNumbers`. Static shell: `<nav class="gws-toc"
data-gws-toc='{"minLevel":"h2","maxLevel":"h3"}'>`. New shared
`BuildTableOfContentsRuntimeScript()` + `LayoutContainsTableOfContents(layout)` guard.
Tests: unit (static shell markup/data-attrs, guard true/false); browser (new
`CmsTableOfContentsBrowserTests.cs`, `PlaywrightBrowserFixture`) — list populates matching page
headings, links scroll correctly, optional scrollspy highlights the active item.

**`reading-progress`** — Props: `color` (raw hex) + `colorToken` (resolved via
`WidgetStyle.ResolveColor`), `height` (px, default 4), `position` (`top`/`bottom`, default
`top`). Verify during implementation whether `RenderWidget`'s dispatch already receives `tokens`
or needs threading. New shared `BuildReadingProgressRuntimeScript()` +
`LayoutContainsReadingProgress(layout)` guard.
Tests: unit (resolved color as inline custom property, raw and token cases, guard tests); browser
(scroll the page, assert the bar's rendered width increases).

### Critical files

- `CmsBlockHtmlRenderer.cs`, `CmsBuilderEditor.razor` (every phase)
- `Program.cs` (Phase 2 related-posts wiring + Phase 4 script fix)
- `RelatedArticlesService.cs` (new, Phase 2)
- `CmsSectionTemplates.cs` (Phase 3)
- `GlobalBlockOverridableFields.cs` (Phases 1-2)
- `wwwroot/cms-public.css` + `public-site.css` (every phase)
- `CmsBlockHtmlRendererTests.cs`, `RelatedArticlesServiceTests.cs`,
  `CmsTableOfContentsBrowserTests.cs`/`CmsReadingProgressBrowserTests.cs`

### Explicitly out of scope

No new `Author` entity/migration; no newsletter/ESP integration; no changes to `/blog`/
`/blog/{slug}` behavior beyond extracting `GetRelatedArticlesAsync` into a reusable service.

---

## Workstream C — Marketing-page block library

### Context and reference material

Research already complete: `reports/Marketing-theme module inspiration.md` fetched and analyzed
9 real ThemeForest marketing/business themes (Brooklyn, Rayo, Engitech, NextSaaS, Stratus,
Inotek, Codera, SaaSapp, Stackly) plus one CodeCanyon admin-dashboard template (Dhonu, kept as a
separate future backlog for an internal dashboard UI kit, not this workstream). It synthesized a
**20-item recurring module palette** that shows up regardless of stated niche. This workstream
turns that palette into real `LayoutWidget` types using the exact same mechanism as Workstream B
(same `CmsBlockHtmlRenderer`/`CmsBuilderEditor.razor`/`CmsSectionTemplates.cs` touch-points — see
Workstream B's "Architecture recap," not repeated here).

Plus three specific additions approved 2026-09-26 as an addendum to that report, chosen for how
much they reuse existing subsystems:

1. **Booking/scheduling embed** — this app already has a full Scheduling feature under CRM
   (`admin/scheduling`); embedding the existing booking widget on a public page is near-zero new
   backend work.
2. **Form presets** (quote request, appointment request, contact) — identical pattern to
   Workstream B's newsletter-signup: a `CmsSectionTemplates.cs` entry pre-seeding the existing
   generic `form` widget with different fields.
3. **FAQ block with schema markup** — the `accordion` widget already exists; add FAQPage JSON-LD
   structured-data output alongside its existing HTML. Confirmed during an earlier audit: **no
   JSON-LD/structured-data of any kind exists anywhere on this app's public pages today** — a
   real, currently-missing SEO win.

Deliberately deferred (not rejected, from the same addendum): a cookie-consent banner
(compliance-sensitive, build carefully when picked up) and a popup/announcement bar (scope to one
dismissible bar, not a full popup engine). Explicitly not recommended: chat widgets, live
"social proof" visitor trackers, or anything requiring a live third-party account.

### Priority tiers (mirrors the report's own framing)

**Tier 1 — near-free, build first** (reuse existing subsystems almost entirely):
- FAQ + schema markup (extend existing `accordion`)
- Booking/scheduling embed (wrap existing CRM scheduling widget)
- Form presets: quote request, appointment request (extend existing `CmsSectionTemplates`
  pattern, mirrors Workstream B's newsletter-signup)

**Tier 2 — new static/structural widgets, similar effort to Workstream B's Phase 1**:
- Hero variants (video-background, split hero) — likely Props additions to the existing `hero`
  widget type (verify current `hero` widget's Props during implementation) rather than new types
- Stats/counter block (animated number counters)
- Logo cloud / integration showcase
- Team grid — **a `CmsSectionTemplates` entry already exists** (`team-grid`); check whether it
  needs promotion to a first-class widget type with a detail-page variant, or stays a section
  template
- Process/steps widget (numbered step-by-step)
- Tabs widget (generic tabbed container — features/pricing/compatibility)
- CTA banner — **a `CmsSectionTemplates` entry already exists** (`cta-banner`); same
  promote-or-keep question as team-grid

**Tier 3 — data/interaction widgets, similar effort to Workstream B's Phase 2/4**:
- Pricing table with tabbed monthly/yearly variant — **a `CmsSectionTemplates` entry already
  exists** (`pricing-table`); extend with the tab toggle
- Portfolio/project grid with filtering + detail-page layout
- Case study block (heavier narrative variant of portfolio grid)
- Testimonial slider variant (a `testimonial-row` section template already exists as a static
  grid; slider is a new interactive variant, likely reusing Workstream B's TOC/reading-progress
  precedent for a lightweight inline script rather than a JS library dependency)
- Generic slider/carousel (image/content, independent of hero/testimonials)
- Gallery/image grid

**Tier 4 — needs its own scoping pass before building** (genuinely new integration surface, not
just a new widget type): Google Map/location embed (needs a maps provider decision — free-tier
OpenStreetMap embed vs. Google Maps API key, matching this app's established "prefer free/no-key"
convention), header/footer builder (a bigger architectural change — configurable nav/footer
regions treated as their own composable module, not page content; scope as its own mini-plan
before starting).

### Explicitly out of scope for this workstream

Header/footer builder is listed but **not scoped in detail here** — it's structurally different
from every other item (a site-wide chrome region, not page content) and deserves its own short
design pass before implementation; don't start it opportunistically mid-workstream. The Dhonu
admin-dashboard findings (kanban, invoice screen, calendar, chat, ticketing) are a separate
future backlog for an internal dashboard UI kit, not this page-builder workstream.

### Verification

Same standard as Workstream B: `CmsBlockHtmlRendererTests.cs` additions per widget, full solution
build (zero warnings), full test suite, `./scripts/verify-release.sh` before considering a tier
done.

---

## Workstream D — 15 unique visual themes

### Current architecture (confirmed by direct code read, 2026-09-27)

`DesignTokenSet` (`src/GwsBusinessSuite.Application/CmsBuilder/DesignTokenModels.cs`) is a
`record(Colors, TypeScale, SpacingScale)` stored as raw JSON in **one field per site**
(`CmsSite.DesignTokensJson`, confirmed in `CmsBuilderModels.cs:31`). `AppearanceCustomize.razor`
edits that single set directly — there is **no preset library, no theme picker, and no concept of
multiple named themes to choose between** today. This is a from-scratch feature, not an extension
of an existing picker.

### Market research grounding

- **Astra** (WordPress) ships 250-300+ starter-site templates across multiple page builders — the
  "many templates" end of the spectrum. [Source: WebSearch, Astra Review/comparison articles,
  2026.]
- **Kadence** ships ~45 curated starter templates, positioned as a smaller, more curated
  alternative to Astra's volume approach. [Source: WebSearch, Kadence vs Astra comparisons,
  2026.]
- The user's own ask (15 themes) sits closer to Kadence's curated-quality model than Astra's
  volume model — 15 is a deliberately small, high-craft set, not an attempt to compete with a
  250-template marketplace.
- **Oqtane's theme-swap mechanism is the right architectural pattern to borrow, not its
  compiled-assembly packaging.** Per `reports/Oqtane CMS architecture research.md`: switching
  between *already-installed* themes is "a pure runtime, data-driven admin operation — a dropdown
  selection... picked up on next render with no compile step and no restart," while *installing a
  new* theme package requires a restart (irrelevant here, since this app's "themes" are data
  presets, never compiled assemblies — there's no equivalent "install a new package" step at
  all). The transferable idea is specifically: **theme identity lives in data (a foreign key /
  named reference on the site), not in compiled code**, so switching is instant and requires zero
  deployment.

### Design decisions

1. **A "theme" = a named, curated bundle of:** one `DesignTokenSet` (colors/type scale/spacing)
   + a default homepage `PageLayout` composed from Workstream B/C's block palette + (optionally)
   a small set of recommended `CmsSectionTemplates` picks. This is why D is sequenced after B/C —
   a theme with only the current 6 section templates to draw from would be thin; 15 genuinely
   distinct-feeling themes need the fuller block palette.
2. **New entity: `CmsThemePreset`** (name, description, thumbnail, `DesignTokensJson`, a
   `DefaultHomepageLayoutJson` or a reference to a `CmsSectionTemplates`-style factory) — a
   read-mostly catalog, not user-editable data (ship 15 hand-designed presets as seed data /
   code-defined records, mirroring `CmsSectionTemplates.All`'s own "static factory list" shape
   rather than a database table with an admin CRUD screen, at least for v1).
3. **Applying a theme = a one-time copy, not a live binding.** Selecting theme #7 copies its
   `DesignTokensJson` into the site's `CmsSite.DesignTokensJson` and (optionally, admin-confirmed
   since it can overwrite existing page content) seeds a starter homepage layout — it does NOT
   create an ongoing "this site follows theme #7" relationship the way Oqtane's per-site theme
   foreign key does. This avoids a much bigger scope: live re-theming, migration of
   already-customized pages, and theme-version-drift handling. Flag this explicitly to the user
   as a deliberate simplification versus Oqtane's live-binding model, worth revisiting only if a
   real need for "site always tracks theme X's updates" emerges.
4. **Fifteen concrete theme concepts** (to be refined with the user, not treated as final):
   variety should span at minimum: a minimal/neutral default, a bold/high-contrast agency style,
   a soft-pastel/wellness style, a dark-mode-first tech/SaaS style, a classic serif/editorial
   style, an ultra-minimal typography-led style, a vibrant-gradient startup style, a
   corporate/enterprise style, a warm/local-business style, and others spanning the niches the
   Workstream C research already covered (agency, IT services, SaaS). Exact 15 names/palettes are
   a design task, not an engineering one — flag as a follow-up design pass once C's block palette
   exists to compose from.

### Phases

**Phase 1 — `CmsThemePreset` model + a picker UI.** New record type, a static seed list starting
with 2-3 presets (not all 15) to prove the mechanism, a picker screen in
`AppearanceCustomize.razor` (or a new dedicated page) showing thumbnail + name + "Apply" button,
wired to the copy-on-apply behavior in decision 3.

**Phase 2 — Full palette of 15 presets.** Once B and C ship enough block variety, design and
encode the remaining ~12-13 presets. This is the bulk of the work but is pure data/design
authoring against an already-proven mechanism from Phase 1 — low technical risk, real design-time
cost.

**Phase 3 — Thumbnail generation.** Each preset needs a representative preview image. Decide
during Phase 1 whether this is a hand-designed static image per theme (simplest, matches this
app's existing pattern of hand-authored assets) or a generated screenshot (more infrastructure,
not recommended for v1).

### Verification

Unit tests asserting every seeded `CmsThemePreset` has valid, parseable `DesignTokensJson` and a
non-empty name/description (mirroring `CameraCoverageRegionsTests.cs`'s own "every entry in a
static list is well-formed" pattern). Manual verification: apply each theme to a scratch site,
confirm the copy-not-bind behavior (editing the site after applying a theme doesn't retroactively
change if the preset is edited later — it shouldn't, given decision 3).

---

## Workstream E — BuddyPress-style community/intranet system

### Market research grounding

- **BuddyPress** (free WordPress plugin): the baseline community primitives — member profiles,
  activity streams, groups, private messaging, friend connections. [Source: WebSearch, 2026
  BuddyPress theme roundups.]
- **BuddyBoss** (paid theme/plugin suite built on BuddyPress): adds a more polished, mobile-app-
  ready social-network layer on top of the same primitives. [Source: WebSearch, 2026 comparisons.]
- **Woffice** (the theme the user explicitly named as inspiration): the most feature-rich
  intranet/extranet product surveyed — "custom login page, project management system, wiki, chat
  and messaging, file manager, event calendar, staff directory, forum, built-in drag-and-drop
  dashboard builder, distinct department access levels, secure corporate messaging," built as a
  BuddyPress-based theme rather than a from-scratch social framework. [Source:
  [woffice.io feature pages](https://woffice.io/woffice-blog-3-top-community-wordpress-themes/),
  [wpmayor.com intranet build guide](https://wpmayor.com/build-complete-intranet-extranet-woffice/)]

### What this app already has (confirmed by direct code inspection, 2026-09-27) — reuse, don't rebuild

- **Wiki system** (`WikiPage` and related — confirmed present via existing browser tests and
  memory of the "Quick Note dashboard feature"/"User guides doc set" work) — directly covers
  Woffice's "wiki" pillar already. No new wiki subsystem needed; extend/reuse the existing one.
- **Comments subsystem** (`src/GwsBusinessSuite.Application/Comments/` — `CommentModels.cs`,
  `CommentService.cs`, `ICommentService.cs`, confirmed present) — a real building block for
  activity-feed-style discussion threads; check its current scope (likely tied to CMS
  pages/articles today) before assuming it generalizes to arbitrary community posts.
- **`SentinelNotification` entity** (`CoreEntities.cs:698`) — an existing notification-delivery
  precedent, currently scoped to the Sentinel assistant feature. Evaluate during Phase 1 whether
  to generalize this into a shared notification pipeline or build a parallel one for community
  events — generalizing is preferred if its schema isn't Sentinel-specific, to avoid two
  notification systems.
- **`AppUser`** (`CoreEntities.cs:14`) — the existing staff/admin account model. Confirmed
  **minimal**: `Username`, `PasswordHash`, `Role`, `IsActive`, lockout/MFA fields — **no display
  name, avatar, bio, or any profile field**. A member profile needs genuinely new data, not an
  extension of a couple of nullable columns.

### What genuinely does not exist yet (confirmed absent by direct grep, 2026-09-27)

No `DirectMessage`/`ChatThread` entity, no `Department`/`Team`/`Group` entity, no member-directory
UI, no activity-feed concept. These are the real net-new scope of this workstream.

### Design decisions

1. **Scope this as an internal staff/employee intranet, not a public-facing social network** —
   matches "Woffice" specifically (an intranet/extranet product) rather than "BuddyBoss" (a
   public community-site product), and matches this app's existing single-tenant, admin-portal-
   centric architecture. Members are `AppUser` accounts (extended with a new profile), not a new
   public-registration user class.
2. **New `MemberProfile` entity** (1:1 with `AppUser`): display name, avatar, bio, job title,
   department reference, phone/extension, social/contact links. Keep `AppUser` itself unchanged
   (auth concerns) — profile is a separate table, matching this codebase's general pattern of
   keeping auth-critical entities minimal (seen in `AppUser` itself).
3. **New `Department`/`Team` entity**: name, description, a list of member `AppUser` ids. Powers
   both the staff directory (Woffice's "directory extension") and Oqtane's
   `EntityName:PermissionName:DefaultRoles` idea flagged in the Oqtane research as "worth studying
   directly" for department-scoped permissions (e.g., a department lead can edit their own
   department's wiki pages without being a full admin) — evaluate adopting a similarly
   dynamically-composed permission-policy shape rather than hardcoding a fixed role enum, since
   this app's existing `AppRoles`/`Role` string field is a small fixed set today and department-
   scoped delegation is a new kind of permission need.
4. **Activity feed**: a new `ActivityEvent` entity (actor `AppUser`, verb, target reference,
   timestamp) populated by hooking into existing actions (wiki page edited, comment posted, CRM
   record updated) rather than a bolt-on separate tracking system — reuse existing audit/
   `AuditableEntity` patterns already present across this codebase's other entities where
   possible instead of inventing a new logging convention.
5. **Direct messaging**: new `ChatThread`/`ChatMessage` entities, 1:1 and group threads between
   `AppUser`s. Given this app already has a `SentinelNotification` precedent and (per earlier
   session memory) a Blazor Server "iMessage-style" chat UI already built for the Sentinel
   assistant (`ChatEditor`/`ChatEditorHandler`), evaluate reusing that same chat UI component
   for human-to-human messaging rather than building a second chat interface from scratch — a
   real opportunity to cut this workstream's cost significantly, and worth a dedicated Explore
   pass at the start of Phase 1 specifically to check how tightly that component is coupled to
   the Sentinel/AI use case before committing to reuse.
6. **Explicitly deferred from Woffice's full feature list**: project management system, built-in
   CRM/HRM tools, learning management system, e-commerce integration — this app already has its
   own CRM elsewhere in the suite (don't duplicate it inside the community system), and PM/LMS
   are substantial standalone products, not intranet-social features; propose them as separate
   future asks if wanted, don't fold them into this workstream's scope.

### Phases

**Phase 1 — Foundations.** `MemberProfile`, `Department` entities + migrations; a staff directory
page (list/search/filter by department); profile view/edit pages. Explore pass on the existing
Sentinel chat component's reusability (decision 5) happens here, before Phase 4 commits to an
approach.

**Phase 2 — Activity feed.** `ActivityEvent` entity; hook points into wiki/comments/CRM actions;
a feed UI (likely a new dashboard widget or its own page).

**Phase 3 — Groups/departments as a permission surface.** Extend department membership into an
actual access-control dimension (decision 3's dynamically-composed permission idea) — scope this
phase's exact mechanism as its own short design pass once Phase 1's `Department` entity exists,
rather than fully speccing an authorization-policy redesign sight-unseen here.

**Phase 4 — Direct messaging.** `ChatThread`/`ChatMessage` entities; UI (reused or new, per
Phase 1's Explore finding); real-time delivery (check whether this app already has a SignalR/
Blazor Server circuit-based push mechanism to reuse — Blazor Server's own circuit model may make
this simpler than a typical REST+websocket chat build).

### Verification

Standard pattern per phase: migrations tested against SQLite in-memory (matching this codebase's
established EF Core test convention), full test suite, `./scripts/verify-release.sh`. Given this
workstream touches auth-adjacent surface (`AppUser`-linked profiles, department-scoped
permissions), give Phase 3's permission changes extra manual verification — log in as a
non-admin department member and confirm they can/can't access what's expected, matching this
project's own established "MFA/permission changes need a real login-flow check, not just unit
tests" discipline (see the Blazor cookie-auth and mandatory-MFA memories from earlier work on
this app).

---

## Housekeeping (tracked, not active engineering work)

- **Dependabot PRs failing CI**: several `dotnet-dependencies` bumps to
  `GwsBusinessSuite.Application` (`Microsoft.CodeAnalysis.CSharp` and others) are failing their
  own CI checks. Handle opportunistically — review each failure's actual cause before merging or
  closing, don't blanket-merge or blanket-close.
- **`sentinel:read` end-to-end live check**: flagged after the SentinelGPT consolidation work as
  still outstanding — this is the user's own manual verification step, not an engineering task
  for this plan.

## References

- [National Highways: Traffic camera/CCTV services](https://nationalhighways.co.uk/roads-and-travel/live-travel-updates/traffic-cameracctv-services/)
- [BC Gov: Province opens up highway camera data to tech community](https://news.gov.bc.ca/releases/2016TRAN0045-000360)
- [github.com/bzsasson/traffic-camera-sources](https://github.com/bzsasson/traffic-camera-sources)
- [Colorlib: 20 Best BuddyPress Themes for Community Sites 2026](https://colorlib.com/wp/best-buddypress-wordpress-themes/)
- [Woffice: 7 Best Community WordPress Themes for 2026](https://woffice.io/woffice-blog-3-top-community-wordpress-themes/)
- [wpmayor.com: Intranet building guide using WordPress, BuddyPress and Woffice](https://wpmayor.com/build-complete-intranet-extranet-woffice/)
- [onlinemediamasters.com: Astra Review 2026 — Starter Templates](https://onlinemediamasters.com/astra-review/)
- [wpallimport.com: Kadence vs Astra](https://www.wpallimport.com/kadence-vs-astra/)
- `reports/Marketing-theme module inspiration.md` (this repo, 2026-09-24 research + 2026-09-26
  addendum)
- `reports/Oqtane CMS architecture research.md` (this repo)
- This session's own live-verified Overwatch provider implementations (2026-09-26/27) as the
  established build/test/DI pattern for Workstream A.

## Verification standard (applies to every workstream)

Per this repo's `CLAUDE.md`: before ending any work session that changed code, run
`./scripts/verify-release.sh` (build + full test suite, including Playwright). For multi-phase
workstreams, finish and verify the *whole* phase before stopping, not a half-applied slice. Do
not push without an explicit go-ahead per phase, matching this project's established manual-push
workflow.
