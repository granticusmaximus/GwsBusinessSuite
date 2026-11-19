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
- [x] Confirm live discovery counts (19-11-26, production logs): until fed9fac1/9bd76edf every search
      hit Ollama's paid API and failed with 402 (694 failures that day); since then searches run on
      SearXNG with no failures, about 1 new configured-publisher page per run (23 accepted over 8
      runs that day). The Sources page layout needs an MFA sign-in, so it wasn't viewed here.

Focused crawler/background/CSS coverage passed 12/12. `scripts/verify-release.sh` completed
`PARTIAL`: responsive lint, whitespace, Compose rendering, restore, dependency audit, zero-warning
Release build, Mac Debug build and all 4,459 tests passed. Responsive and deployed checks were not
run because no targets were supplied. Production discovery remains unchanged until Grant commits
and pushes; live SearXNG searches, publisher yield and the authenticated Sources layout therefore
remain deployment acceptance items.

## Adaptive discovery and useful-yield diagnostics (17-11-26)

- [x] Persist a next-run and zero-yield streak for every configured standing search.
- [x] Keep productive searches at ten minutes; exponentially back repeated zero-yield searches off
  to one hour; retry failed searches after five minutes without stopping the other watches.
- [x] Retain high-priority article queuing ahead of feed, sitemap, homepage and link-traversal work.
- [x] Show the complete search funnel on Sources: searches, raw results, configured-publisher
  results, genuinely new queued URLs and failures.
- [x] Run focused regressions and the complete release gate.

Focused crawler/background/CSS coverage passed 12/12. The final `scripts/verify-release.sh` result
was `PARTIAL`: responsive lint, whitespace, Compose rendering, restore, dependency audit,
zero-warning Release build, Mac Debug build and all 4,459 automated tests passed. Responsive and
deployed checks were not run because no targets were supplied. Live useful-yield counts and the
authenticated Sources layout remain deployment acceptance, and no publication action was taken.
