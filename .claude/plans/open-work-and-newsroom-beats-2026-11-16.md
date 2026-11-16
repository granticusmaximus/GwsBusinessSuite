# Open work review and newsroom beats (16-11-26)

Grant requested a review of open plans and implementation of remaining work. Started from a
clean checkout at `d691dc6e`, with `HEAD..origin/main` equal to zero after fetching.

## Review

The shared tracker contains 65 plans and historical handoffs. Unchecked boxes alone are not a
reliable backlog: later progress entries and current code supersede several of them.

| Area | Remaining work / disposition |
| --- | --- |
| World News trust and newsroom | N5-N15 have domain/storage code, but their UI/endpoints are missing. B10 reading features remain. Extend the existing backend, not a second desk/store. |
| Logging and Resolve | Operational fixes and code-fix approval/audit flows, daily digest, agent log tools and per-job traces remain in the logging plan. Production-changing actions need the planned approval boundary. |
| World News / Overwatch expansion | Unit 1 is still unchecked, but regional outlets and the two-outlet operational threshold already exist. Reconcile the remaining adaptive-threshold requirement before changing trust gates. Units 2-6 are recorded complete. |
| Overwatch camera availability | Provider policy/capability persistence and the dependent UX are still unchecked. Source terms and capture permissions are prerequisites. |
| Mac acceptance | Native map/renderer plans retain device acceptance items. Windows cannot verify installed Mac app behavior. Later MapKit work supersedes parts of the old WebKit recovery proposals. |
| Older delivery plans | Known gaps, app-wide UX, mail, calendar and the ten Overwatch features have completion entries. Optional proposals, unavailable providers and historical checklists are not automatically new implementation requirements. |

Primary references: `world-news-trust-and-crawler-2026-11-13.md`,
`logging-and-resolve-2026-11-12.md`, `world-news-overwatch-expansion-2026-11.md`,
`overwatch-camera-availability-and-world-news-2026-11-09.md`,
`droplet-security-and-overwatch-ten-2026-11-13.md`, and
`sync-and-overwatch-closure-2026-11-10.md`.

## Implementation unit: newsroom beats (N5)

Expose the existing per-person region/topic beats and bounded story queue on News desk.
Reuse the pipeline's beat alerts and bell/push event; preserve its corroboration and hold gates.
Regions retain the existing source-country meaning and must not imply event geolocation.
Fix any beat ownership or alert-retry defects exposed by enabling this workflow.

No schema change, new service, network source, publishing action or remote Git write.
Other newsroom features remain separate units; this does not close N5-N15 as a group.

## Progress

- [x] Fetch and confirm current, clean source; inspect plan inventory and relevant implementations.
- [x] Verify N5 backend/pipeline exists and the desk has no beat management/queue UI.
- [ ] Add beat controls, personal queue and explicit loading/error states.
- [ ] Test ownership, validation, matching/order and deferred alert eligibility.
- [ ] Update the World News user guide and parent plan.
- [ ] Run focused tests and `scripts/verify-release.sh`; record exact results and blockers.
