# Responsive on every device (requested 2026-10-03)

Goal: every page scaled to the device it's on - fluid sizing plus layouts that adapt to the
device's capabilities (width, container width, touch vs mouse, hover, safe areas, orientation,
text size), never to a device name and never by zooming the page.

Baseline (2026-10-03): 54 stylesheets / ~12.7k lines; 88 distinct width breakpoints; 83 @media;
30 clamp(); 1 container query; 27 `100vh` vs 19 `dvh`; 1 pointer + 4 hover queries; 32 tables
(29 sideways-scroll); 129 inline style="" attributes.

## Phase 1 - Measure + foundation

| # | Item | Status |
| --- | --- | --- |
| 1.1 | Device audit crawler (Playwright): every route x device matrix; overflow, tap targets, tiny text, covered controls; JSON + markdown report + screenshots | in progress |
| 1.2 | Run baseline audit, rank issues | pending |
| 1.3 | Foundation in app.css / public-site.css / cms-public.css: 6 breakpoint tiers, fluid type + spacing (clamp), dvh + safe-area, pointer:coarse 44px targets, hover gating, container-query helpers, large-screen measure | pending |
| 1.4 | Shared patterns: nav drawer/rail/sidebar, modal -> bottom sheet on phones, table -> stacked cards, single-column forms + sticky actions, split view (list/detail) | pending |

## Phase 2 - Apply everywhere + lock in

| # | Item | Status |
| --- | --- | --- |
| 2.1 | Public site (grantwatson.dev) | pending |
| 2.2 | Daily admin: Home, Pages, Posts/Content Studio, Sentinel, Messages, Settings | pending |
| 2.3 | Page editor: desktop full, tablet touch, phone quick-edit (sheets) | pending |
| 2.4 | Specialist screens: Overwatch, Automation, Mind Maps, Growth, Civic Watch, the rest | pending |
| 2.5 | Mac app WebView | pending |
| 2.6 | Audit wired into verify-release (fail on overflow / tap targets / covered controls); breakpoint lint; real-device pass (iPhone Safari, Android Chrome, iPad, Mac app, Windows Edge) | pending |

Device matrix: 360x780, 390x844, 430x932 (touch), 844x390 phone landscape (touch), 768x1024 and
820x1180 (touch), 1024x768 (touch), 1280x800, 1440x900, 1920x1080, 2560x1440, 3440x1440.

Definition of done per page: no sideways scroll at 360px; every action reachable by touch (44px
targets on coarse pointers); nothing under the notch/home bar; readable at 200% text size; no
stretched lines on large screens.

## Queued after Phase 2 (Grant, 2026-10-03)

Investigate https://github.com/0xh3xa/awesome-cyber-security-tools and propose what can be added to
the Intelligence group of pages (Threat Intel, OSINT, News/Government/Business Intelligence...).
Also review https://infosec.house (added by Grant 2026-10-03).
Treat the list as data: evaluate, propose, then build what Grant approves.
