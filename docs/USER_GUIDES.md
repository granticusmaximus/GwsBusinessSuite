# User Guides

This is the onboarding knowledge base for GWS Business Suite — a step-by-step, "how do I use
this" guide for every section of the admin app, the Client Portal, and the public site. It's
aimed at a new user learning the app for the first time, and at an existing user looking up how a
specific feature works.

This is distinct from the rest of `docs/` (`ARCHITECTURE.md`, `WORKFLOW_AUTOMATION.md`,
`GROWTH_STUDIO.md`, etc.), which document engineering design and delivery history for
contributors, not day-to-day usage for operators.

**Also live inside Sentinel itself**: every guide below is mirrored into a "User Guides" folder
in Sentinel (`/admin/sentinel`), so you can browse them from inside the app you're already
working in, with working links between guides, instead of only reading them as files in this
repository. That in-app copy is regenerated automatically on every deploy from the exact
Markdown source here (`Program.cs`'s `EnsureUserGuidesInSentinelAsync` startup step) — editing a
guide page directly inside Sentinel will be overwritten the next time the app restarts, so treat
these files as the only real source of truth and edit them here, not there.

**A note on screenshots**: `AUTOMATION_USER_GUIDE.md` includes real screenshots captured from a
live, logged-in instance. Mandatory MFA on every login (no dev bypass) blocks scripted/automated
login without a live TOTP secret, so the guides below are text-only for now. They can be
illustrated later in a pass where screenshots are captured manually, or with a temporary MFA
accommodation.

**Keeping this current**: every major feature change should update the guide for the section it
touched, as part of that same change — not as separate follow-up work. If a change doesn't fit
cleanly into an existing guide's structure, that's a signal the guide's structure needs to change
too, not that the update should be skipped.

## Guides

### Home

- **Dashboard** (`/admin`) and **Mission Control** (`/admin/mission-control`) — both covered in
  [Platform Operations](PLATFORM_OPERATIONS_USER_GUIDE.md), alongside the other operational
  pages they pull data from.

### Publishing

- [Content Creation](CONTENT_CREATION_USER_GUIDE.md) — Posts, Content Studio, SEO Audit,
  Localization, SentinelGPT Page Builder, Approval Queue.
- [Site Building](SITE_BUILDING_USER_GUIDE.md) — Pages (Canvas Studio), Appearance, Menus, Media
  Library.
- Growth Studio (`/admin/growth`) — see [`GROWTH_STUDIO.md`](GROWTH_STUDIO.md) for the product
  spec; an operator-facing companion guide is planned.

### Relationships

- [CRM & Relationships](CRM_USER_GUIDE.md) — CRM, Deal Scoring, Billing, Scheduling, Email
  Campaigns, Comments, User Management.
- [Support](SUPPORT_USER_GUIDE.md) — the admin ticket inbox and the Client Portal support
  experience.

### Intelligence

- [Sentinel & SentinelGPT](SENTINEL_USER_GUIDE.md) — the internal Notion-style workspace and its
  embedded AI assistant.
- [SentinelCLI](SENTINELCLI_USER_GUIDE.md) — the macOS terminal coding agent for
  analyzing and making reviewable changes in one local repository or a directory of repositories.
- [Intelligence & BI](INTELLIGENCE_USER_GUIDE.md) — Media Watch, Civic Watch, Podcast Directory,
  Business Intelligence dashboards, and the OSINT Watch public-source map.

### Operations

- [Workflow Automation](AUTOMATION_USER_GUIDE.md) — the visual automation builder (existing
  guide, screenshot-illustrated).
- [Affiliate Operations](AFFILIATE_OPERATIONS_USER_GUIDE.md) — CJ Ads, Affiliate Suggestions,
  Affiliate Analytics.
- [Platform Operations](PLATFORM_OPERATIONS_USER_GUIDE.md) — Builder Reference, Live Show, Docker
  Management, Security Audit, Privacy Operations, Settings.

### Client Portal & Public Site

- [Client Portal & Public Site](CLIENT_PORTAL_USER_GUIDE.md) — what a contact/client sees and can
  do outside the admin app.

## Contributing to a guide

Match the shape already established by `AUTOMATION_USER_GUIDE.md` and `SUPPORT_USER_GUIDE.md`:
a short intro naming the URL prefix the guide covers, a numbered table of contents, a
"Core concepts" section defining the domain vocabulary before any how-to steps, then one section
per real workflow a user would actually walk through, and a closing "Known limitations" section
that says plainly what the feature doesn't do yet rather than staying silent about gaps.
