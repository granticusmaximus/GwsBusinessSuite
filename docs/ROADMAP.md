# Roadmap

## Phase 1 ✅
- Dashboard shell
- SQLite persistence
- CRM contacts
- Wiki markdown pages

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

## Phase 5 — Big Vision
- Ingest WordPress and Elementor Pro documentation as Ollama reference material
- Use Ollama to suggest features and generate UI/logic based on those docs
- Progressively copy over WordPress/Elementor features without proprietary code

## Other Areas to Address
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
  (STUN-only, no TURN server, sized for a handful of invited viewers) signaled over a new
  `LiveShowHub` (SignalR); viewers open an unauthenticated, expiring invite link
  (`/watch/{token}`) with no account needed. Each show is recorded client-side
  (`MediaRecorder`) and uploaded in sequential chunks to disk, then listed for replay at
  `/admin/live-show-recordings`. Known limitation: STUN-only means a viewer behind a
  strict/symmetric NAT may fail to connect - no TURN relay is configured.
- Wiki history tracking ✅ — already fully shipped (commit `d514a56`): every save is a
  real git commit via LibGit2Sharp (`WikiService.cs`), with a full History/Diff/Revert UI
  already in `Wiki.razor`. The roadmap simply hadn't been updated. Page hierarchy (parent
  page selector, ordered tree) and a title/slug search box have since shipped too
  (`Wiki.razor`). Remaining real gap is "richer editing": the editor is a plain textarea
  with no wiki-links autocompletion, image embedding, or TOC generation.
