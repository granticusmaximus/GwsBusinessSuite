# Roadmap

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
- Sentinel (Notion-class connected workspace) 🚧 — renamed from Knowledge Base/Wiki with
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
  database templates are delivered; distributed scale-out and CRDT/OT co-authoring remain;
  permissions and public sharing are delivered, while richer guest/teamspace administration remains;
  Sentinel AI/agents; and
  current-API/two-way interoperability.
