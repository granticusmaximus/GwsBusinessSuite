# Content Creation — User Guide

This is the complete guide to GWS Business Suite's Content Creation tools: **Posts**
(`/admin/article-editor`), **Content Studio** (`/admin/content-studio`), **SEO Audit**
(`/admin/seo-audit`), **Content Localization** (`/admin/localization`), and the **SentinelGPT Page
Builder** (`/admin/app-generation`) with its **Approval Queue** (`/admin/app-generation-queue`).

This guide is text-only (no screenshots) — see the note at the end of [`docs/USER_GUIDES.md`](USER_GUIDES.md)
for why, and how that may change later. For the engineering design behind the AI article
generator, see [`SEO_ARTICLE_GENERATOR.md`](SEO_ARTICLE_GENERATOR.md); for the branding rule
applied to AI-generated hero images, see [`ARTICLE_IMAGE_BRANDING.md`](ARTICLE_IMAGE_BRANDING.md).

## Contents

1. [Core concepts](#core-concepts)
2. [Posts: the article list](#posts-the-article-list)
3. [Writing and publishing a post manually](#writing-and-publishing-a-post-manually)
4. [CJ ad placements on a post](#cj-ad-placements-on-a-post)
5. [Content Studio: researching trends](#content-studio-researching-trends)
6. [Generating a draft with SentinelGPT](#generating-a-draft-with-sentinelgpt)
7. [The draft workspace: review, revise, approve, publish](#the-draft-workspace-review-revise-approve-publish)
8. [Revising an already-live post with SentinelGPT](#revising-an-already-live-post-with-sentinelgpt)
9. [Running an SEO audit](#running-an-seo-audit)
10. [Translating content with Content Localization](#translating-content-with-content-localization)
11. [How a published translation actually appears live](#how-a-published-translation-actually-appears-live)
12. [Generating a new page with the SentinelGPT Page Builder](#generating-a-new-page-with-the-sentinelgpt-page-builder)
13. [Reviewing and approving generated pages](#reviewing-and-approving-generated-pages)
14. [Who can access what](#who-can-access-what)
15. [Known limitations](#known-limitations)

---

## Core concepts

This cluster works with several distinct content records, and it matters which one you're
editing:

- **Article** — a blog post (`/blog/{slug}`). This is what Posts and Content Studio both
  ultimately produce. An article has a `Status` of `Draft` or `Published`, a `Source` of `Manual`
  or `OllamaGenerated`, and an optional `SourceDraftId` linking it back to the Content Studio
  draft it was published from.
- **CMS page** — a structured page built from sections/columns/widgets (`BlocksJson`), served at
  `/cms/{siteSlug}/{pageSlug}`. SEO Audit, Localization, and the SentinelGPT Page Builder all work
  with CMS pages as well as articles; day-to-day page editing itself happens in Canvas Studio
  (Pages), covered in the Site Building guide.
- **Content Studio draft** (`SeoArticleDraft`) — a separate, richer working copy used only while
  an SentinelGPT-generated article is being written and reviewed. It has its own status
  (`PendingReview` → `Approved`/`Rejected`), its own revision history, and its own affiliate
  placement scoring. Publishing a draft is what turns it into (or updates) a real Article.
- **Content localization** — a translated copy of one article's or one CMS page's title, body, and
  meta description, keyed by content + a language code (e.g. `es`). It has its own `Draft`/
  `Published` status independent of the source content's own status.
- **App generation request** — a chat-style conversation with SentinelGPT that produces a plan to
  add one or more new CMS pages to a site. Nothing is created in the CMS until an admin approves
  the plan from the Approval Queue.

Two Ollama-backed engines do the heavy lifting behind all of this: a general chat/generation model
(used for drafting articles, translating text, and planning pages) and, optionally, the same or a
different model used specifically for SEO Audit's "AI-era readiness" opinion. All of it runs
against your own locally-installed Ollama models — no article text, translation, or page plan is
ever sent to an external AI API.

## Posts: the article list

`/admin/article-editor` (nav label "Posts") is the master list of every Article regardless of how
it was created — written by hand here, or generated and published from Content Studio.

- **Filters**: Status (Draft/Published), Source (Manual/SentinelGPT Generated), Category, and a
  live title search.
- **Sortable columns**: Title, Status, Published date, Updated date (click a header to sort, click
  again to reverse).
- A **Published** article whose `PublishedAt` is set in the future shows a **Scheduled** badge
  instead of Published, and its public "View" link is hidden until that time arrives — the article
  simply isn't publicly visible yet (see [Known limitations](#known-limitations) for what
  "scheduled" does and doesn't do).
- **Publish/Unpublish** inline links toggle status directly from the list without opening the
  article.
- **Bulk actions**: select rows with the checkboxes, then "Move to Trash" as a batch.
- **Trash**: trashed articles move to a separate Trash view (toggle button in the header) where
  you can Restore them or Delete Permanently. Permanent deletion is only allowed from Trash — you
  can't skip straight from a live article to a hard delete.
- **Manage Categories**: a small panel to add or delete article categories. Deleting a category
  that's still in use doesn't touch those articles — they simply fall back to "Uncategorized."
- **New Article** creates a blank article titled "Untitled Article" with an auto-generated
  timestamped slug, applies your site's default category and author byline from Settings, and
  takes you straight into the editor.

## Writing and publishing a post manually

Opening an article (`/admin/article-editor/{id}`) gives you:

- **Metadata**: Title (typing it also live-updates the Slug, unless you've edited the slug by hand
  — once you do, auto-slug generation stops for that article), Author, Topic, Primary/Secondary
  Keywords, Category, Tags, Meta Description (with a running character counter against the
  150–160 char target used by SEO Audit), Estimated Reading Time (free text, e.g. "6 min read"),
  and a **Publish At** date/time — set it in the future to schedule the article (see the list-page
  note above on what "scheduled" actually means today).
- **Hero image**: upload a file directly (stored as a data URI, capped by the site's configured
  max upload size) or set an external Hero Image URL instead (a URL, if set, overrides an
  uploaded image), plus alt text and an optional caption.
- **Body**: a Markdown editor (the same WYSIWYG editor used throughout the suite) with a live
  preview pane rendered side by side.
- **Save Draft** vs **Save & Publish** — the latter sets Status to Published and stamps
  `PublishedAt` to now if it isn't already set to a future date; **Unpublish** (shown once
  published) moves it back to Draft.

Publishing also kicks off a best-effort background pass that tries to match affiliate offers to
the article's content — a failure here never blocks the publish itself, and any suggestions it
finds are reviewed separately on the Affiliate Suggestions page, not here.

## CJ ad placements on a post

The "CJ Ad Placements" card lets you drop CJ affiliate offers into an article's body:

1. **Add Ad Placement** — pick an advertiser (grouped from your imported CJ offers — see the CJ
   Ads page if the list is empty), then a specific offer, then a call-to-action label.
2. Saving creates a placement with a unique **slot token** (`{{CJ_AD_...}}`-style).
3. **Insert at Cursor** drops that token into the Markdown body wherever your cursor currently
   sits in the editor.
4. The token is resolved into real ad markup dynamically when the article is served — editing,
   moving, or removing a placement afterward never requires republishing the article itself.

## Content Studio: researching trends

`/admin/content-studio` is a three-step workspace for generating a full article with SentinelGPT,
separate from the Posts editor's manual workflow.

**Step 1, Research Current Trends** pulls currently trending posts from Hacker News and dev.to
(optionally narrowed to a Focus Area), then asks SentinelGPT to summarize the community's
positive/negative takes and propose specific article angles. Results are cached for 4 hours;
**Force Refresh** skips that cache. Each suggested angle has a **Use This Brief** button that
pre-fills the Article Brief fields below it (topic + primary/secondary keywords) — you still
review and adjust before generating.

## Generating a draft with SentinelGPT

**Step 2, Article Brief** takes Topic, Target Audience, Primary Keyword, and Secondary Keywords,
then **Generate Draft With SentinelGPT** streams the article live into a preview pane as Ollama
writes it — full generation can take several minutes on local (CPU-only) hardware, and the
complete draft is only persisted once generation finishes. If SentinelGPT flagged any factual
claims it couldn't verify while writing, you're told the count and pointed at the draft's Source
Notes to review before publishing. When generation completes you're taken straight into that
draft's workspace.

**Step 3, Persisted Drafts** lists every draft with a Status filter (All/PendingReview/Approved/
Rejected) and a topic search. Per-draft actions: **Open** (into the draft workspace), **Not
interested** (rejects the draft with a note, without opening it), and **Delete** (permanent, with
a confirmation prompt).

## The draft workspace: review, revise, approve, publish

`/admin/content-studio/draft/{id}` is where a Content Studio draft is actually worked through to
publication:

- **Generated Article** — the Markdown body in the same WYSIWYG editor, with an "Unsaved edits"
  badge while it differs from the saved version. **Save article edits** persists your changes
  immediately, with no SentinelGPT regeneration involved.
- **Hero Image** — upload one directly, or **Generate with SentinelGPT**: experimental, currently
  macOS-only, and requires a hero-image-capable model configured under Settings → SentinelGPT
  first (the button is disabled until one is set).
- **Revision Notes** feeds three of the four workflow actions below.
- **Approve Draft** marks it Approved (required before Publish is even enabled — the server
  enforces this too, not just the button's disabled state). **Publish** writes the draft live: it
  creates a new Article, or updates the existing one **matched by slug** if this draft has been
  published before, so revising and republishing never creates a duplicate post. The raw
  `{{CJ_AD_*}}` tokens are kept unresolved in the saved Markdown (resolved dynamically at serve
  time, same as manual posts), and publishing hands you off into the Posts editor for that article
  for any further day-to-day edits. **Request Revision** sends your notes back to SentinelGPT to
  regenerate the draft (requires notes to be filled in first). **Reject Draft** marks it Rejected.
- **Affiliate Placements** shows each placement's advertiser, category, impressions, clicks, CTR,
  and a 7-day trend indicator (a tooltip breaks down current vs. previous 7-day CTR). Opening an
  offer link also records a click interaction against that placement for the same trend metrics.
- **Revision history** lets you view a diff against any past revision, or **Restore** one — this
  creates a **new** pending-review revision rather than rewriting history, the same "restore, don't
  rewrite" semantics as Workflow Automation's version rollback.
- **Workflow history** is a plain audit trail of every event on the draft (submitted for review,
  published live, manually edited, revision restored, etc.) with who did it, when, and any notes.

## Revising an already-live post with SentinelGPT

If an article in the Posts editor has `SourceDraftId` set (i.e., it originated from Content
Studio), its header shows **View SentinelGPT Draft** (jumps back to that draft's workspace) and
**Revise with SentinelGPT** — a panel right there in the Posts editor. Enter revision notes and
click **Regenerate & Republish**: this runs the exact same "request a revision, then publish"
sequence as the draft workspace, against that same underlying draft, and republishes it live in
place. You don't have to leave the Posts editor to make a SentinelGPT-assisted change to a live
AI-authored post.

## Running an SEO audit

`/admin/seo-audit` runs a **deterministic checklist** against one article or CMS page at a time,
optionally blended with an **AI-era readiness** opinion from a local Ollama model.

Pick a piece of content, optionally a target keyword override (falls back to the article's own
Primary Keyword if left blank), and optionally an Ollama model name. **Run Audit** produces a
score out of 100 and a checklist:

| Check | What it evaluates |
| --- | --- |
| Title length | 30–60 characters is the target range |
| Meta description length | 120–160 characters is the target range |
| Word count | Fail under 150, Warning under 300, Pass at 300+ |
| Heading structure | Counts H2+ subheadings — 0 fails, 1 warns, 2+ passes |
| Image alt text | Every image needs alt text (content with no images auto-passes) |
| Slug | Short, lowercase, hyphenated, ≤60 characters |
| Links | At least one non-image link present |
| Keyword coverage | Only run if a keyword is set — checks title, meta description, and body |

If you supply a model name, SentinelGPT separately scores three 0–10 dimensions — a clear early
answer, AI-parseable structure, and citable specificity — meant to gauge how likely an AI answer
engine (ChatGPT, Perplexity, Google AI Overviews) would extract and cite the content, along with a
short summary and concrete suggestions. When present, that AI score is blended in at 30% weight
against the deterministic score's 70%. If the model is unreachable or its output can't be parsed,
the audit still completes using the deterministic score alone — the AI pass is best-effort, never
a hard requirement.

Every run is saved, and once you've run more than one on the same content, a small History list
shows score-over-time. For CMS pages specifically, if the page's stored layout JSON is malformed,
the audit reports that as an explicit Fail finding rather than silently scoring it as empty
content.

## Translating content with Content Localization

`/admin/localization` manages translated copies of articles and CMS pages, independent of the
source content's own draft/published state.

Pick a content item, then either:

- **Generate** — enter a language code (e.g. `es`, `fr`, `ja`) and an Ollama model, and
  SentinelGPT translates the title, body, and meta description together in one pass (translated
  as a single ordered batch so nothing gets mismatched). For a CMS page, only specific known
  widget fields are translated — hero headline/subline/CTA labels, heading/paragraph text,
  richtext content, button labels, card title/body, testimonial quote/author fields, and image
  alt/caption — not accordion or form-widget content (see [Known limitations](#known-limitations)).
  The result is always saved as a **Draft** for you to review, never auto-published.
- **Add manually** — write or paste a translation yourself, no AI involved.

Each translation in the table shows its language, Draft/Published status, whether it was AI- or
manually-produced (and which model, if AI), and when it was last updated. **Edit** opens it in the
same panel used to add one — an article's body uses the WYSIWYG Markdown editor, while a CMS
page's body is edited as raw JSON in a plain code-safe textarea specifically so its layout
structure can't be accidentally mangled by rich-text conversion. **Publish**/**Unpublish** toggles
a translation's status; a human edit to a translation (via Save) always clears its AI provenance,
even if it started as an AI draft. **Delete** removes a translation permanently, with a
confirmation prompt.

## How a published translation actually appears live

Publishing a translation does **not** create a separate URL. A published translation only appears
when a visitor requests the original content's normal public URL — `/blog/{slug}` for an article,
or `/cms/{siteSlug}/{pageSlug}` for a CMS page — **with a `?lang={code}` query string appended**.
If a Published translation exists for that content and language code, its title, body, and meta
description are swapped in for that request; otherwise the page renders in its original language
as usual, silently ignoring an unrecognized or unpublished `lang` value.

## Generating a new page with the SentinelGPT Page Builder

`/admin/app-generation` (nav label "SentinelGPT Page Builder") is a chat-driven way to plan new
CMS pages for a site — nothing here touches the CMS directly.

1. Choose a **Target Site** and describe what you want ("a pricing page with three tiers and a
   comparison table"), then **Start Chat**.
2. SentinelGPT replies conversationally, informed by the site's existing page titles (so it avoids
   obvious duplicates) and up to three relevant notes pulled from the suite's internal CMS
   knowledge library. Once it has a concrete plan, it attaches the **full current plan** (not a
   diff) as structured page/section/widget JSON, restricted to a fixed set of widget types (hero,
   heading, paragraph, richtext, button, image, card, testimonial, spacer, divider) with
   placeholder image paths, since no image generation is available in this flow.
3. Keep replying in the chat box to refine the plan — each turn regenerates the complete page set
   from scratch, so later turns can also remove or rework earlier pages, not just add to them.
4. Once at least one page has a plan, **Submit for Approval** moves the request out of your hands
   and into the Approval Queue. While a request is Pending Approval or has already been Approved/
   Rejected, it's read-only here — you can review it but not keep chatting.

## Reviewing and approving generated pages

`/admin/app-generation-queue` (nav label "Approval Queue") lists every request currently Pending
Approval, for an admin to review before anything reaches the CMS.

Selecting a request shows its full chat transcript and the proposed pages (title, slug, meta
description, section count). **Approve** commits every proposed page to the target site in a
single all-or-nothing transaction — a failure partway through rolls every page back rather than
leaving a half-applied plan — and marks the request Approved with who approved it and when.
Approved pages are created as **Drafts** in the CMS, exactly like a page built by hand in Canvas
Studio: nothing goes live automatically, and you still edit, arrange, or publish them from the
Pages screen (see the Site Building guide). **Reject** marks the request Rejected with an optional
reason, visible back to whoever started the chat.

## Who can access what

Access is role-gated per page, not uniform across this whole cluster:

| Page | Required role |
| --- | --- |
| Posts (Article Editor) | Admin, Author, or Contributor |
| Content Studio | Admin, Author, or Contributor |
| SEO Audit | Admin or Contributor (not Author) |
| Content Localization | Admin or Contributor (not Author) |
| SentinelGPT Page Builder | Admin, Author, or Contributor |
| Approval Queue | Admin only |

An Author account can write and publish posts and use Content Studio and the Page Builder, but
cannot run an SEO audit, manage translations, or approve a generated page plan.

## Known limitations

- **"Scheduled" is a display convention, not an enforced state.** Setting a future Publish At
  hides the article's public "View" link and its Published-list badge shows "Scheduled," but
  there's no background job that flips anything at that time on its own — the underlying
  visibility check (`PublishedAt` in the future) is evaluated live on each request to `/blog/...`.
- **Content Studio's hero-image generation is experimental and macOS-only**, and requires a
  separately configured hero-image model under Settings → SentinelGPT before it's even enabled.
- **CMS page translation only covers a fixed set of flat widget fields.** Accordion and form
  widgets carry nested JSON values that v1 of translation doesn't reach — those stay untranslated
  even after a full "Generate."
- **No on-page language switcher or `hreflang` tags.** A published translation is only reachable
  by a visitor (or a link you build yourself) appending `?lang={code}` to the original URL —
  there's no UI on the public site itself that surfaces which languages are available.
- **SEO Audit's AI-era readiness pass is best-effort.** If the chosen Ollama model is unavailable
  or returns output that can't be parsed as the expected JSON, the run silently falls back to the
  deterministic checklist score alone rather than failing outright.
- **The SentinelGPT Page Builder always regenerates the full plan each turn**, not an incremental
  diff — a long refinement conversation can occasionally have SentinelGPT drop or reshuffle an
  earlier page's structure between turns rather than only adding to it.
- **Approved generated pages land as Drafts, unattached to any menu.** Approval creates the CMS
  page rows; adding them to site navigation or publishing them live is a separate, manual step in
  Canvas Studio.
- **The Page Builder only creates new pages.** It can't edit or extend an existing CMS page's
  layout through chat — that's Canvas Studio's job.
