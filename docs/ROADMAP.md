# Roadmap

`RELEASE_READINESS.md` is the authoritative GWS Business Suite 1.0 scope, priority, and
acceptance contract. This roadmap records feature history and longer-term parity work; a
feature marked delivered here is not production-verified unless its current release evidence
also passes.

## Phase 1 ✅
- Dashboard shell
- SQLite persistence
- CRM contacts
- Sentinel workspace (originally Wiki markdown pages)

## Phase 2 ✅
- SEO Article Generator powered by Ollama
- Article approval, rejection, and revision queue
- CJ affiliate ingestion and integration
- Image Generator powered by Ollama: Content Studio can generate hero images using a
  separately configured image-capable Ollama model, retain generation provenance, and
  replace generated images with manual uploads.

## Phase 3 ✅
- CMS builder
- Block editor
- Site preview
- Custom CSS per site and page
- Contact form with submission handling
- Revision history for CMS pages
- CMS builder is a combination of WordPress features and Elementor Pro features
- Cross-frame canvas editing browser verification: palette widgets, global blocks, and
  in-preview reordering now persist and refresh immediately in Chromium, including a
  fallback handshake for browsers that omit the iframe `drop` event
- Static export: download a CmsSite as a ZIP of static HTML/CSS files, including
  nested page paths, publicly-visible pages only, and bundled media assets

## Phase 4
- Ollama local bridge ✅ — OllamaConsole already covered connectivity/model-list/prompt
  testing; added model management (pull/delete) to close the one real gap
- Docker image build automation ✅ (dev-only) — "Build Image" button added to
  `/admin/docker-health`, calls the existing `DockerDeploymentService`. Only works when
  running locally with Docker installed; production deploys still happen via
  `docker compose up -d --build` over SSH (`.github/workflows/deploy.yml`), unchanged.
- AI app generation approval queue ✅ — an Author picks a target `CmsSite`, iteratively
  chats with Ollama to refine a page plan (`/admin/app-generation`), then submits it for
  an Admin to review the transcript and page preview and approve/reject
  (`/admin/app-generation-queue`). Approval creates real `CmsPage` rows (as Drafts, not
  auto-published) via the existing `ICmsBuilderService`; nothing is ever applied without
  human sign-off. See `AppGenerationService.cs` for the Ollama prompt contract and the
  defensive JSON-plan parsing.

## Phase 5 — Big Vision ✅
- Ingest WordPress and Elementor Pro documentation as Ollama reference material ✅ —
  scoped to expanding the existing hand-authored CmsKnowledge library rather than scraping
  (avoids any WordPress.org GPL / Elementor Pro proprietary-content licensing question
  entirely). Added 6 more clean-room entries covering capabilities this app didn't have
  yet: dynamic content loops, popup builder w/ trigger rules, custom fields, theme-builder
  header/footer templates, widget areas/sidebars, and conditional display rules. See
  `SeedMoreCmsKnowledge` in `ApplicationDbContext.cs`.
- Use Ollama to suggest features and generate UI/logic based on those docs ✅ — wired as
  retrieval-augmented context rather than a new standalone tool: `AppGenerationService`
  now runs the latest chat message through `ICmsKnowledgeService.SearchAsync` and folds
  the top 3 matching entries into the Ollama system prompt as "Reference notes," so
  chat-drafted pages benefit from WordPress/Elementor-inspired workflow patterns without
  any new UI or approval step.
- Progressively copy over WordPress/Elementor features without proprietary code ✅ (first
  one) — a "posts-grid" widget (WordPress's core "loop" concept) added to the CMS
  builder's widget vocabulary: a live, always-current grid of the most recently published
  Articles, configurable count/columns/image/excerpt/CTA. `CmsBlockHtmlRenderer` stays a
  pure function (no DB access) - `PublicArticleSummary` data is fetched once per request
  by each of Program.cs's three render call sites (live site, static export, admin
  preview) and threaded through.

## Other Areas to Address
- Growth Studio (Plausible/Matomo/GA-inspired analytics and social publishing) 🚧 — the
  privacy-first collection layer, indexed dashboard, custom-event hook, SentinelGPT
  channel variants, encrypted Facebook/X/LinkedIn credentials, direct text publishing,
  scheduling, queue, delivery history, named event/destination conversion goals, and
  ordered funnel/drop-off reporting, saved audience segments, session-scoped report filters,
  new/returning retention cohorts, and privacy-reviewed country/region reporting backed by
  a locally hosted GeoIP database are delivered. Report exports,
  social OAuth/media, and engagement imports are staged in
  `docs/GROWTH_STUDIO.md`.
- Workflow Automation (n8n-class, clean-room) 🚧 — foundational graph persistence,
  immutable publish versions, protected credential references, execution history, core node
  registry, and visual Blazor editor are tracked in `docs/WORKFLOW_AUTOMATION.md`. Advanced
  parity work remains explicitly staged there rather than copying n8n source or assets.
- Article approval/revision queue ✅ (audited) — the approve/reject/revision UI and the
  full workflow-event-history timeline were already complete. Two real gaps found and
  fixed: the drafts list silently capped at 20 most-recent-by-`CreatedAt`, so an old
  pending-review draft could fall off behind newer approved/rejected ones (now sorts
  pending-review first); and there was no pending-count badge in the nav despite the
  identical pattern already existing for Comments/Docker (now added). Revision history is
  now append-only for generated revisions, manual edits, and restores; the draft workspace
  includes line-level diffs and non-destructive rollback.
- Live Show page ✅ — expanded from a local camera/mic self-monitor into real streaming.
  "Go Live" starts a `LiveShowSession` and opens a direct broadcaster<->viewer WebRTC mesh
  (sized for a handful of invited viewers) signaled over a new
  `LiveShowHub` (SignalR); viewers open an unauthenticated, expiring invite link
  (`/watch/{token}`) with no account needed. Each show is recorded client-side
  (`MediaRecorder`) and uploaded in sequential chunks to disk, then listed for replay at
  `/admin/live-show-recordings`. TURN relay support now covers viewers behind strict or
  symmetric NAT: the server mints short-lived coturn REST credentials, both broadcaster
  and viewer receive the same configured ICE pool, and Docker provides an opt-in coturn
  override with automatic production activation when the required `.env` values exist.
- Sentinel (Notion-class connected workspace) ✅ v1 / 🚧 broader parity — renamed from Knowledge Base/Wiki with
  `/admin/sentinel` as the canonical route. Delivered foundations include nested block pages,
  DB-snapshot history/diff/restore, `[[Page]]` links, ranked and highlighted workspace search,
  backlinks, per-user favorites/recents, structured person/date mentions with a personal
  inbox, editable Table/Board databases, and encrypted manual/hourly Notion
  import. Full capability parity is now explicitly staged in `docs/WIKI_NOTION_CLONE.md`:
  database rows-as-pages plus List/Gallery are now delivered; remaining views,
  persisted page/block discussions, replies, resolution, reactions, and collaboration
  notifications, live cross-circuit refresh, and heartbeat page presence are now delivered;
  optimistic content-version checks now prevent silent concurrent overwrites with explicit
  draft recovery; block-level discussion pins now surface open threads directly in the editor
  and jump to a focused conversation/composer; reusable page templates now create independent
  pages with fresh block identities, and transactional page-tree duplication copies nested pages
  beside the source with independent revisions; database duplication and reusable source-independent
  database templates are delivered; reusable block templates now capture live editor content and
  insert independent blocks with fresh identities; database formulas now evaluate typed expressions
  with logical operators plus numeric, text, and date functions discoverable from the property editor,
  relations use canonical row selection with optional reciprocal properties synchronized from either side,
  and rollups calculate count/numeric/unique aggregates without persisting stale computed values;
  distributed scale-out and CRDT/OT co-authoring remain;
  permissions and tokenized public sharing are delivered; Sentinel workspace membership is
  limited to authenticated portal accounts, and external viewers use public shares rather than
  guest portal accounts;
  Sentinel AI/agents; and
  current-API/two-way interoperability. Database rows now carry the same icon, cover image, and
  bounded version history (20/row, diff, revert-as-new-version) as regular pages, closing the
  "database page layouts and presentation" phase in `docs/WIKI_NOTION_CLONE.md`. A follow-up UI
  audit found this area's CSS had lagged its own markup (the row-peek panel was effectively
  unstyled) - now fixed, and two smaller visual-polish gaps (`SentinelTemplates` leaning on raw
  Bootstrap chrome; `SentinelDiscussions`/`SentinelPresence` base colors only correct when nested
  under `.sentinel-workspace`) are tracked in that doc rather than silently left undocumented.
  The "native editing polish" phase's foundation is also delivered: database-page autosave
  (debounced, silent, and deliberately separate from version-history checkpoints), session-local
  undo/redo, and a contextual block actions menu (duplicate/move/delete) - the last two landed in
  the shared block editor module, so the main Sentinel page editor gained them too. The "media
  and presentation" phase is now partially delivered: the embed block recognizes YouTube, Vimeo,
  Spotify, Figma, CodePen, and Loom URLs and renders them as a sandboxed iframe (URL-pattern
  detection only, not a live oEmbed metadata fetch, to avoid an SSRF surface), and cover images
  can be uploaded directly from the page/row cover picker instead of requiring a trip through the
  Media Library first. Custom icon-image uploads, page width/font controls, and reusable
  workspace-level visual defaults remain open, tracked in `docs/WIKI_NOTION_CLONE.md`.
  "Database automations" now has its trigger half delivered - a database's rows can start a
  Workflow Automation (the existing n8n-class engine in `docs/WORKFLOW_AUTOMATION.md`, reused
  rather than duplicated) when a row's properties change, gated to active workflows only, with
  per-workflow failure isolation so one broken automation can't block a row save or its
  sibling workflows. Schedules and credentialed integrations needed no new work since the
  automation engine already covers both generically. A write-back action node (a workflow
  updating a database row) remains open. "Real-time collaboration" now shows remote cursors:
  an in-memory `SentinelCursorTracker` broadcasts which block each other connected viewer is
  currently in (a colored name-pill, not a character caret), reusing the same process-local
  fan-out pattern discussions/notifications already use rather than adding a SignalR hub.
  True character-level CRDT/OT concurrent editing is deliberately not attempted - a subtly
  wrong text-merge implementation risks corrupting user content, which is a materially
  different risk profile than a missing feature, so it stays explicitly open in
  `docs/WIKI_NOTION_CLONE.md` rather than being rushed. "Knowledge navigation" gained saved
  searches (bookmark and re-run a workspace search) and database-row mentions (`@`-mention a
  row inline, reusing the exact same rich-text link scheme person/date mentions already use;
  a row's own page shows a "Mentioned in" panel mirroring the existing page Backlinks panel).
  Graph navigation (an interactive workspace visualization) remains open - it has no existing
  code to extend and is a substantial feature of its own. Sentinel AI now streams answers live
  (new `IOllamaService.GenerateStreamAsync`, no streaming precedent existed in this app before)
  and cites which pages/databases were actually folded into each run's grounding context,
  replacing the old "dump 30 recent pages into every prompt" approach with the same ranked
  search the sidebar uses. Transcription capture (needs a new self-hosted speech-to-text
  dependency Ollama can't provide) and chained/reviewable multi-step AI workflows remain open.
  The Notion connector's import fidelity is substantially improved per
  `.claude/plans/generic-napping-axolotl.md`'s Track A: `people`/`files`/`relation`/
  `created_by`/`last_edited_by`/`last_edited_time` properties now target Sentinel's real typed
  properties instead of one flattened text string, with relation values resolved from raw
  Notion page ids to local row ids (`NotionSyncService.ResolveRelationRowIdsAsync`, retried
  every sync so a relation to not-yet-imported content still resolves later); `column_list`
  imports as a single native Columns block instead of losing the side-by-side layout entirely;
  Notion video/audio blocks render as inline players; table cells keep bold/italic formatting;
  and `link_to_page` blocks are now visible (a placeholder referencing the Notion id) instead
  of silently dropped. `formula`/`rollup` deliberately stay best-effort text - translating
  Notion's own formula syntax into Sentinel's expression language is a separate project, not a
  value-mapping fix. Track A4 is now delivered: the connector modal can browse all discoverable
  page/database metadata through the encrypted stored token or an unsaved replacement token,
  render it as a collapsible checkbox tree, and persist checked roots through the existing
  subtree-aware selected-id scope. The connection foundation now also supports Notion OAuth:
  short-lived admin-bound state, encrypted access/refresh tokens, token rotation,
  revoke-before-disconnect, workspace identity, and manual internal/PAT fallback. Track A5
  (incremental sync progress) is now also delivered - a manual/webhook sync reports live
  "N of Total items synced" via a new `NotionSyncProgress` callback threaded through
  `NotionSyncService.SyncAsync` - completing Track A. Track B (visual/interaction parity) turned
  out to be mostly already shipped when re-verified against the real code on 2026-08-01; only
  sidebar drag/reparent + per-row Duplicate/Delete and a Ctrl/Cmd+K that actually searches
  Sentinel content (rather than only the app-wide static nav) were real gaps, both now closed.
  See `.claude/plans/generic-napping-axolotl.md` for the item-by-item detail. The staged
  ground-up workspace rebuild and bounded v1 scorecard are specified in
  `docs/SENTINEL_REBUILD.md`. Broader parity gaps remain in `docs/WIKI_NOTION_CLONE.md`;
  current whole-suite acceptance is tracked only in `docs/RELEASE_READINESS.md`.
