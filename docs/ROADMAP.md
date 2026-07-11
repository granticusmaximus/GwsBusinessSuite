# Roadmap

## Phase 1 ✅
- Dashboard shell
- SQLite persistence
- CRM contacts
- Wiki markdown pages

## Phase 2 ✅ (mostly complete)
- SEO Article Generator powered by Ollama
- Article approval, rejection, and revision queue
- CJ affiliate ingestion and integration

## Phase 2 — Remaining
- Image Generator powered by Ollama: `SeoArticleDraft` has hero-image fields scaffolded
  (`HeroImagePrompt`, `HeroImageProvider`, etc.) but nothing reads/writes them yet — the
  only working hero-image path today is manual file upload
  (`ContentStudioDraft.razor`'s `HandleHeroImageUpload`). Not actually built.

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
- Article approval/revision queue — workflow events exist, audit completeness of the queue UI
- Live Show page — exists in admin, needs review and possible expansion
- Wiki — has markdown pages; consider history tracking and richer editing
