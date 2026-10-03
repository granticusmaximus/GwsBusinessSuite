# October 2026 batch (requested 2026-10-03)

Single tracker for the "what's left" batch. Update each item's status as it lands; every item is
verified with `./scripts/verify-release.sh` before it is marked done. Grant commits manually.

| # | Item | Status |
| --- | --- | --- |
| 1 | Email delivery: every feature uses whatever is configured (any SMTP section in .env, else the connected Google account via Gmail API); status card + test send; startup log line | done 2026-10-03 (verify-release PASS) |
| 2 | Remove the Notion connection (connector settings, OAuth, sync job, picker). Keep already-imported Sentinel pages | done 2026-10-03 (verify-release PASS) |
| 3 | Map/location widget - free, no API key (OpenStreetMap embed) | done 2026-10-03 (verify-release PASS) |
| 4 | Header/footer builder | built + browser-verified; final verify running |
| 5 | Department-based permissions (Community Phase 3) | pending |
| 6 | Small leftovers: Osiris durable Wikidata cache; Developer Mode folder memory | pending |
| 7 | Community gaps: group conversations, message attachments, unread notifications, multiple departments, more activity-feed sources | pending |
| 8 | Email gaps: per-list digest mode; per-campaign (not global) unsubscribe for drip sequences | pending |
| 9 | Overwatch coverage: research + add free, key-less sources for unresearched states/countries | pending |
| 10 | Housekeeping: refresh stale master-plan status; triage failing Dependabot PRs | pending |
| 11 | Hand Grant: Turnstile key steps | pending |

Out of scope by Grant's instruction: macOS installer (disregarded 2026-10-03).

| 3a | FOUND BUG: 9 CMS widget runtimes + home blog grid + reveal + submitted-modal were inline scripts the CSP blocks in production; now served externally | done 2026-10-03 (verify-release PASS) |

## Notes

- 3a: CJ affiliate script (anrdoezrs.net -> yceml.net, 440 KB, runtime-built XHR targets) is also
  CSP-blocked. Left alone deliberately - allowing a third-party script on every public page is
  Grant's call.

- 1: prod logs (2026-10-02) showed the SMTP-based features unconfigured. Grant believes Gmail SMTP
  (gwatson117@gmail.com) or grant@gwsapp.net is set somewhere; no droplet access from here, so the
  fix makes every source usable and shows which one is active.
