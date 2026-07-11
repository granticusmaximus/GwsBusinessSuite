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
- AI app generation approval queue — no code exists for this at all. Its closest
  precedent (App Registry / Deployments) was deliberately deleted (commit `87d3c68`) as
  "stubs... no longer aligned with the project direction." Needs a product decision on
  what this actually generates before any code should be written.

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
  identical pattern already existing for Comments/Docker (now added). Remaining, smaller
  items not addressed: "revision" has no
  diff/rollback — it's an AI regenerate-and-overwrite with no version history.
- Live Show page — reviewed. It's a real, working feature but a narrow one: a local
  browser camera/mic self-monitor only (getUserMedia preview), with no backend service,
  no persistence, and no actual streaming/broadcast output. "Expansion" needs a product
  decision first (streaming destination? persisted "shows"? viewer-facing UI?) before
  any code should be written — same category as the AI app generation queue.
- Wiki history tracking ✅ — already fully shipped (commit `d514a56`): every save is a
  real git commit via LibGit2Sharp (`WikiService.cs`), with a full History/Diff/Revert UI
  already in `Wiki.razor`. The roadmap simply hadn't been updated. Remaining real gap is
  "richer editing": no page hierarchy (no parent/order), no search box, and the editor is
  a plain textarea with no wiki-links/image embedding/TOC.
