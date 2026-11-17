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
