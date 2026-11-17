# World News active scraping

## Goal

Make World News actively discover current reporting instead of relying only on feed and link traversal, while retaining the crawler's publisher allow-list, robots.txt handling, refusal pauses, pacing, and URL deduplication.

## Progress

- [x] Trace the existing feed, crawler, import, clustering, translation, and Overwatch pipeline.
- [x] Search periodically for current world and regional reporting through configured web search.
- [x] Admit only configured publisher hosts and enqueue discovered article URLs at high priority.
- [x] Add focused discovery tests and update the World News user guide.
- [x] Carry complete stored English translations and summaries into Overwatch and its downstream context.
- [x] Keep news refresh and English processing alive independently after a transient worker failure.
- [x] Run the complete release verification gate (`PARTIAL`: all local checks passed; deployed-site targets were not configured).

## Active standing watchlist (17-11-26)

Grant clarified that World News must actively seek current articles on a regular set of subjects,
not merely rotate one broad search and otherwise depend on feeds.

- [x] Search a configurable standing watchlist covering world regions plus conflict, severe weather,
  economy, technology/cyber, health/science, climate/energy and politics/elections.
- [x] Run up to 12 watchlist searches every 10 minutes and rotate through the complete list.
- [x] Deduplicate results, admit only configured publishers and keep priority crawling behind the
  existing robots, refusal, pacing and daily-limit controls.
- [x] Expose whether active discovery is configured, its last attempt, searches today and accepted
  publisher pages today on World News → Sources.
- [x] Run focused crawler/UI contract tests and the complete release gate.
- [ ] Confirm live discovery counts and Sources layout after Grant deploys the staged work.

Focused crawler/background/CSS coverage passed 12/12. `scripts/verify-release.sh` completed
`PARTIAL`: responsive lint, whitespace, Compose rendering, restore, dependency audit, zero-warning
Release build, Mac Debug build and all 4,459 tests passed. Responsive and deployed checks were not
run because no targets were supplied. Production discovery remains unchanged until Grant commits
and pushes; live SearXNG searches, publisher yield and the authenticated Sources layout therefore
remain deployment acceptance items.
