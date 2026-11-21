# Full responsive audit, every page, large screens to phones (21-11-26)

## What Grant asked for
"A full visual audit of this app to verify that every single page is fully responsive from large screens
to small screens. I want you to verify everything after you have done such."

## Tooling (existing, extended)
- `tests/GwsBusinessSuite.Tests/ResponsiveAuditTests.cs` (plan: responsive-every-device.md): opt-in
  crawl of every route on 12 devices (360 phone to 3440 ultrawide) recording sideways overflow (document
  and the .gws-content panel), touch targets, small text, covered controls, console/CSP errors, and a
  screenshot per page and device.
- Found 21-11-26: it audits 51 of the app's 81 parameterless admin pages and 6 public pages. Missing:
  access-denied, touch-id, ai-health, article-refresh, boards, cash-flow, client-impact, content-calendar,
  content-studio, login, logs, loose-ends, my-day, not-found, osint/camera-sources, osint/directions,
  sentinel/window, sentinelgpt/activity, shared-boards, ssh-terminal-window, time, wiki, and seven World
  News pages (about, brief, desk, front-pages, methodology, search, source-book, sources); public /tip,
  client portal (login, home, support), world-news corrections and live, booking.

## Steps
1. Add every missing route (record-specific ones through GWS_AUDIT_EXTRA_ROUTES with ids from the
   scratch database), and a guard test: every parameterless @page route is audited or listed as an
   exception with a reason.
2. Run the audit locally (scratch copy of the database, Development, Chromium and WebKit) on all 12
   devices. Report: audit.md / audit.json + screenshots.
3. Triage: every overflow, covered control, undersized target and script error; and look at the
   screenshots (contact sheets per page) for what the measures can't see (cramped, overlapping, cut off).
4. Fix by page, at the root (shared components first).
5. Re-run the whole audit until clean; final visual review of the contact sheets; release gate.

## Progress
- 21-11-26: plan written; route gap measured (30 admin + about 8 public pages unaudited).
