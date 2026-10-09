# Well-rounded app: six features (requested 2026-10-09)

Grant: "Go forward with these" - the six added in the second round of the whole-app review (web 6-8,
Mac 5-7). The first round's ten (audit log, quotes/e-sign/portal, My Day + notification prefs,
passkeys/SSO, uptime monitoring; Mac notifications/badge, quick-ask hotkey, Share/Shortcuts,
menus/offline) are NOT approved yet - don't start them without asking.

Each item is verified with `./scripts/verify-release.sh` before it's marked done.

| # | Feature | Status |
| --- | --- | --- |
| W6 | Customer history: one timeline on a CRM contact (tickets, invoices, bookings, forms, campaigns, portal, deals) | built 2026-10-09: ContactTimelineService + History card on the contact page |
| W7 | Time tracking -> billable hours -> invoice lines | built 2026-10-09: TimeEntries table (migration AddTimeEntries), /admin/time, contact Time card; void/delete releases hours |
| W8 | Content calendar: posts, email campaigns, drips, social, live shows, podcasts in one view | built 2026-10-09: /admin/content-calendar; drag/Move to reschedules posts/pages/social; podcasts left out (the directory is other people's shows) |
| M5 | Menu-bar status: live counts + health in the menu-bar app | built 2026-10-09: GET /api/v1/sentinel/status (sentinel:read) + Keychain key in the menu-bar app; built Release + launch-checked |
| M6 | Voice/meeting notes: on-device transcription -> local-model summary -> Sentinel page | built 2026-10-09: Notes tab; compiles; NOT run on screen (needs mic/speech permission prompts) |
| M7 | Screen capture to text: on-device OCR -> Sentinel / ticket / CRM / ask SentinelGPT | built 2026-10-09: Capture tab (paste/choose image, Vision OCR, save/ask/copy/CRM); ticket attach not included; compiles, not run on screen |
