# October 2026 batch (requested 2026-10-03)

Single tracker for the "what's left" batch. Update each item's status as it lands; every item is
verified with `./scripts/verify-release.sh` before it is marked done. Grant commits manually.

| # | Item | Status |
| --- | --- | --- |
| 1 | ~~Email delivery: every feature uses whatever is configured (any SMTP section in .env, else the connected Google account via Gmail API); status card + test send; startup log line~~ | done 2026-10-03 (verify-release PASS) |
| 2 | ~~Remove the Notion connection (connector settings, OAuth, sync job, picker). Keep already-imported Sentinel pages~~ | done 2026-10-03 (verify-release PASS) |
| 3 | ~~Map/location widget - free, no API key (OpenStreetMap embed)~~ | done 2026-10-03 (verify-release PASS) |
| 4 | ~~Header/footer builder~~ | done 2026-10-03 (verify-release PASS) |
| 5 | ~~Department-based permissions (Community Phase 3): Sentinel sharing with departments + multiple departments per person (gap 7 part)~~ | done 2026-10-03 (verify-release PASS) |
| 6 | ~~Small leftovers: Osiris durable Wikidata cache; Developer Mode folder memory~~ | n/a - Osiris was removed (6b009a4); folder memory already shipped (b81df78, d741485) |
| 7 | ~~Community gaps: group conversations, message attachments, unread notifications (live bell + 15-min email), more activity-feed sources (multiple departments done in 5)~~ | done 2026-10-03 (verify-release PASS) |
| 8 | ~~Email gaps: weekly digest mode for alert lists; per-campaign unsubscribe for drip sequences (+ FOUND BUG: drip unsubscribe page unsubscribed on page load - link scanners could unsubscribe people; now a confirm step)~~ | done 2026-10-03 (verify-release PASS) |
| 9 | Overwatch coverage: research + add free, key-less sources for unresearched states/countries | IN PROGRESS - WZDx incident batch done 2026-10-04 (14 states); cameras next, see overwatch-coverage-2026-10.md |
| 10 | ~~Housekeeping: refresh stale master-plan status; triage failing Dependabot PRs~~ | done - master plan refreshed; no open Dependabot PRs remain (checked 2026-10-03) |
| 11 | ~~Hand Grant: Turnstile key steps~~ | done (in chat) |

Out of scope by Grant's instruction: macOS installer (disregarded 2026-10-03).

| 3a | ~~FOUND BUG: 9 CMS widget runtimes + home blog grid + reveal + submitted-modal were inline scripts the CSP blocks in production; now served externally~~ | done 2026-10-03 (verify-release PASS) |

| 5a | ~~FOUND BUG: 12 text boxes gated their button on blur (Settings test email, alert test, Sentinel Invite, workflow Grant access, template/API-key/property names, model pull...) - clicking the button did nothing~~ | fixed (oninput) |

| 7a | ~~USER-REPORTED (2026-10-03): page-editor widget delete "saves but doesn't"; editing area too small on a laptop~~ | canvas toolbar now acts on its own widget; labelled Delete at top of inspector; add scrolls canvas to the new block; delete notice; working left-panel collapse (was a dead icon); compact header < 1600px; Fullscreen button; local-time "Draft saved" (was UTC) |

## Notes

- 3a: CJ affiliate script (anrdoezrs.net -> yceml.net, 440 KB, runtime-built XHR targets) is also
  CSP-blocked. Left alone deliberately - allowing a third-party script on every public page is
  Grant's call.

- 1: prod logs (2026-10-02) showed the SMTP-based features unconfigured. Grant believes Gmail SMTP
  (gwatson117@gmail.com) or grant@gwsapp.net is set somewhere; no droplet access from here, so the
  fix makes every source usable and shows which one is active.
