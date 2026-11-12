# Working in GWS Business Suite

This file holds the rules for every assistant that works in this repo: Claude (through
`CLAUDE.md`, which should import it with an `@AGENTS.md` line), Codex (it reads `AGENTS.md`), and
Copilot (through `.github/copilot-instructions.md`, which points here). Change a rule here and not in a
per-assistant file. Each assistant's private memory is invisible to the others, so anything they
all need to know belongs here.

## Shipping

- **Every push to `main` is a production deploy.** There's no draft state. Grant commits and
  pushes by hand, sometimes in the middle of a session, so treat every edit in the working tree
  as something that could ship at any moment. Keep the tree building and the tests passing at
  every step.
- **Before you call work done, run `./scripts/verify-release.sh`.** It runs exactly CI's deploy
  gate (the server and shared projects, then the full test suite with Playwright browser tests)
  and, on a Mac, a Debug build of the Mac app when your branch touches it. About 3-5 minutes
  since 12-11-26 (the old whole-solution build is `--full`, and took 35-50 minutes cold).
  - Don't build the Mac app in Release in its default folders while Grant may run `open gws`:
    both write `src/GwsBusinessSuite.App/bin|obj/Release`, and a mixed bundle crashed the app at
    launch on 12-11-26.
  `dotnet build` and `dotnet test` on their own aren't enough. Report its result. Don't hand
  back while it's still running. Nothing checks locally before a push any more, and CI only
  blocks the deploy after the commit is already on `main`.
  - Two production deploys failed on 02-11-26 for skipping this. One commit removed
    `IKanbanBoardService` members that code still called. A PR checked against months-old code
    added a duplicate type.
  - A result of `PARTIAL` is normal locally: it only means the deployed-site checks weren't run.
- **No pushing, PRs or merges unless Grant gives permission in the current session.** That
  permission covers that session's work only and doesn't carry over to later sessions. Force
  push only when Grant says to force push for that specific case.
- **Zero warnings, and warnings are errors.** `Directory.Build.props` sets `TreatWarningsAsErrors`
  for every project (07-11-26), so a warning fails the build. Fix the cause. If one truly can't be
  fixed yet, suppress it on that one line with `#pragma` and the reason beside it, never with a
  project-wide `NoWarn`. Workflows count too: keep `actionlint` clean, and don't name a
  `secrets.X` that isn't set in the repo. An optional key's line stays commented out until its
  secret is set (see `deploy.yml`). Never read every secret at once (`toJSON(secrets)`): on this
  public repo GitHub holds such a run for approval as possibly malicious, so nothing deploys
  (07-11-26).
- **Finish what you start.** For anything with several sequenced pieces, finish and verify the
  whole unit before stopping. Don't leave it half-applied.

## Start from the current code

- **Branch or worktree from up-to-date `origin/main`.** Run `git fetch` first. Then
  `git rev-list --count HEAD..origin/main` must be `0` before you start work. If you reuse an old
  worktree, merge `origin/main` first.
  - A worktree created on 27-05-26 was reused into November, 816 commits behind. The assistant
    working in it concluded that features already in the app were missing and rebuilt them, and
    its "checked" PR broke production.
- **Look before you build.** This app is large, and "missing" features have repeatedly turned
  out to be built already. Search the code (`git grep`) for an existing service before adding
  one, and extend it rather than adding a parallel system. Examples:
  - mail: `MailService`, `MailTransport`
  - presence: `RecordPresenceTracker`
  - live updates: the `*Notifier` classes
  - OAuth tokens: `AutomationCredentialService`
  - Ollama tool calling: `IOllamaService.ChatAsync`
  - loading states: `LoadingIndicator`, `BusyOverlay`
- **Don't remove public members "for a cleaner interface"** without building every caller first.

## Architecture

- Clean Architecture, with dependencies flowing inward only: Web -> Application,
  Infrastructure -> Application and Domain, Application -> Domain, Domain -> nothing.
  - Domain holds entities, value objects and business rules.
  - Application holds use cases, abstractions, DTOs and validation.
  - Infrastructure holds the database, HTTP clients and external integrations.
  - Web is Blazor UI composition only. Keep components thin, and put business logic in
    Application services.
- Every meaningful feature or rule change comes with tests, at the narrowest layer that proves
  it. Don't keep empty or placeholder tests.
- Fix root causes, not UI-only workarounds. Remove dead code and scaffold leftovers.

## Conventions and known traps

UI:
- **Dates:** every date shown is `dd-MM-yy`, and every time is 24-hour `HH:mm` (`05-11-26 14:05`).
  This covers the public site, emails and the Mac app. Machine formats (ISO, `datetime-local`
  values) stay as they are.
- **CSP:** `script-src` has no `unsafe-inline`, so an inline `<script>` never runs live (inline
  `<style>` is allowed). Put JavaScript in a file under `wwwroot/js`.
- **Loading and busy states:** use `LoadingIndicator` for a page or panel that's loading, and
  `BusyOverlay` while a long job runs. Both are the Overwatch boot screen. Don't use bare
  spinners for these, and don't hand-roll overlays. A small spinner inside a button is fine.
- **Design:** the neumorphic surface system (the `--neu-*` tokens in `app.css` and
  `public-site.css`) has applied to every admin page and the public site since 04-11-26, at
  Grant's request. Build new UI from it, with no hard-coded white panels. Admin breakpoints use
  `@container gws-main`, and `@media` widths are limited to the tiers the lint allows.
- **No `window.prompt`/`confirm`/`alert`:** Cancel on a prompt crashes the Mac app's WebView. Use
  `gwsPrompt.ask`.

Blazor:
- An input that enables a button needs `@bind:event="oninput"`. Plain `@bind` updates only on
  blur, so the button never enables on click.
- String component parameters need `@`: `Title="@_title"`, not `Title="_title"`, which passes
  the literal text.
- `@(expr).Method()` renders `.Method()` as text, because the expression ends at its `)`. Razor
  also reserves `<text>`, so write SVG labels as an encoded `MarkupString`.
- **No `@<>...</>` fragments.** Razor has none: `<>` compiles to an element with an empty name,
  and the browser's `createElement("")` kills the circuit ("An unhandled error has occurred.
  Reload"). That was the Home dashboard straight after login, 01-11 to 05-11-26
  (`KpiCard`). A template with no wrapper element is `@<text>...</text>`.
  `RazorTemplateGuardTests` guards it.
- Scoped CSS: use `::deep` for markup a child component renders. Never use `:global()`.
- Interactive components can't call `SignInAsync`. Sign-in flows use a static page plus a
  minimal-API endpoint (`Login.razor`, `/auth/login`).
- Layout islands (the SentinelGPT side panel, the notification bell) share the page's scoped
  `DbContext` and render for every role. A service that both call uses `IDbContextFactory` per
  call, and business data shown there needs an admin check.
- Overwatch must full-load (`data-enhance-nav="false"`), because enhanced navigation keeps the
  previous page's CSP and Cesium is blocked.

Data and build:
- **SQLite:** EF can't translate `DateTimeOffset` comparisons or `ORDER BY`, so materialize first
  and filter or sort in memory. `CAST(Id AS TEXT)` gives uppercase GUIDs, so compare
  lowercased.
- **EF:** every entity change needs a migration. On a long-lived context, don't add to a
  collection loaded with `Include()`. Add through the child `DbSet` instead.
- **Order before you limit.** A database `Take`/`Skip`/`First` needs an `OrderBy`, or EF warns
  and the rows you get are arbitrary. Use `OrderBy(x => x.Id)` for capped scans and single-row
  settings tables, placed before any `Select`. Load several large collections with
  `AsSplitQuery()`. All of these were fixed on 05-11-26 (about 60 queries). Production logs these
  warnings, so check there after a change.
- **Docker:** a new project referenced by the web app needs its own `COPY` line in the
  `Dockerfile`. `DockerfileProjectsTests` guards this, but `verify-release.sh` doesn't build the
  image.

Mac app (MAUI, Mac Catalyst):
- Don't use a MAUI `Picker`; it crashes on Catalyst. Use `MacMenuPickerHandler`.
- Never re-add `keychain-access-groups`; it stops the app launching.
- Build locally with `-p:ValidateXcodeVersion=false`, but never commit that.

## The environment

- **Production** is a DigitalOcean droplet running Docker.
  - **Outgoing SMTP is blocked** there (ports 25, 465 and 587) until DigitalOcean support lifts
    the block. IMAP (993) and HTTPS work. Mail that has to send from the server uses the Gmail
    API, and an SMTP timeout in production is this block, not a bad password.
  - **Server AI is off** (`ServerAi__Mode: None`). The droplet runs no model. AI work runs on the
    signed-in Mac app through its relays (SentinelGPT, articles, World News summaries and
    embeddings). Server-side AI calls fail with a clear message instead of reaching for an
    Ollama.
    - Background work on **public data** can run on an Admin's signed-in Mac through SentinelGPT's
      agent relay (`MacRelayOllamaService`), but only inside `MacRelayScope.ForSharedWork()`:
      bill overviews, news hot takes, county meeting summaries. Never wrap a person's own content
      in that scope, because it would run on someone else's Mac.
    - While no Admin Mac is online, `ServerAiUnavailableException` means "wait": skip quietly,
      without logging a warning per item.
  - **Sites that refuse the server** (403/405 to a plain request) are left alone for a day
    (`RefusedHostPause`). Respect a refusal. Never get around bot protection with a browser
    user agent or similar tricks.
  - **congress.gov** falls back to the shared `DEMO_KEY` (about 50 requests a day), and the
    Federal Civic feed slows down to fit it. `CongressApi__ApiKey` in `.env` lifts the limit.
- **The Mac app** shows admin pages served by the server. A network connection made from one of
  those pages, such as mail or HTTP, runs on the droplet. A test from the Mac's terminal proves
  nothing about it.
- **Production login needs MFA,** so scripted logins can't get past it. Local development skips
  it.

## Docs and tracking

- **User guides:** with every user-visible change, update the matching `docs/*_USER_GUIDE.md`.
  `docs/` is gitignored because the repo is public, so the files exist only on disk. Edit them
  anyway.
- **Plans:** multi-part work gets a file in `.claude/plans/`, which is git-tracked and is the
  shared tracker for every assistant. Give it a Progress section and keep it current. Write
  handoffs there too, not only in chat.
- **Commit messages:** `type(area): summary`, for example
  `fix(mail): explain blocked SMTP ports`.
