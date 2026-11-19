# World News outage and charge-free search (19-11-26)

Grant asked that World News keep checking for new intelligence, and that nothing use a paid Ollama
service ("build a service that does what that was intending and then have Ollama do its magic").

## Found in production (read-only SSH, `/app/data/logs/*.clef`)

- **Every World News read failed** (98 `World news read failed` from 05:02 to 13:20 UTC on
  19-11-26 in the three newest log files, roughly one per 5-minute read). `FeedSchedule.AfterFailure`
  computed `5 min * 2^(failures-2)` before applying the 1-hour cap. Once one feed had failed ~45
  times in a row, `TimeSpan.FromMinutes` overflowed. The exception escaped the feed loop
  (`Parallel.ForEachAsync`) and aborted the whole read, so no new stories came in.
- **Active discovery searched a paid API.** `.env` has `OLLAMA_WEB_API_KEY`, which made
  `OllamaWebSearchService` use ollama.com's billed web search. It answered 402 Payment Required to
  every search (42 in about 3 hours). `SEARXNG_URL` is set too, and SearXNG answers from the web
  container: a live `time_range=day` search returned BBC, Al Jazeera, CNN, Reuters and AP, with no
  unresponsive engines.

## Fixes

- `FeedSchedule.AfterFailure` caps the exponent before making the `TimeSpan`.
- `WorldNewsPipeline` catches any exception from one feed as that feed's failure (cancellation
  excepted).
- `OllamaWeb:AllowPaidSearch` (default false; compose `OLLAMA_WEB_ALLOW_PAID_SEARCH`). Without it,
  the key is ignored: search is SearXNG, pages are fetched by the server's own guarded client, and
  the gws-ai model does the summarizing and grouping. This existing key-less path is what Grant
  described, so no parallel service was built. If paid search is ever allowed, a 401/402/403
  hands searches and fetches to SearXNG for 6 hours (429: 15 minutes), keyed by API key.
- `WorldNewsService.IsBehind`: the World News header turns amber and says "Not updating", with the
  last good read and the reason, when a read failed or none finished in 3 intervals.

## Progress

- [x] Root causes found in production logs.
- [x] Fixes and tests (`WorldNewsCrawlerTests`: 5,000-failure backoff, a broken feed doesn't stop
      the others, `IsBehind`; `OllamaWebSearchServiceTests`: never paid unless allowed, refusal
      hand-over, no key sent to SearXNG).
- [x] `docs/INTELLIGENCE_USER_GUIDE.md` (Sources section), `.env.example`, `docker-compose.yml`.
- [x] 19-11-26 follow-up (Grant: Today still shows days-old news after a Pull). Production
      `feed_state`: last successful read **15-11-26 05:22**; 199,392 reports since 12-11 never
      grouped, so Today showed 15-11's stories, and Pull's read hit the same overflow. Also 124
      crawled pages were dated in the future (sports fixtures, the newest 22-02-27), and with the
      3-hour window they counted as "recent" and sat on Today. Fixes: `WebCrawler.ToWorldArticle`
      skips a page whose published date is after its fetch (+1 h), and `WorldNewsStore.Prune(before,
      now)` (every read) deletes reports dated over an hour ahead, drops stories left empty and
      resets the rest's `last_seen`. Grouping takes the newest reports first (`RecentArticlesToEmbed`,
      1,500 a read), so Today catches up on the first good read.
- [ ] After deploy: confirm `World news read failed` stops and reads finish every ~5 minutes;
      discovery runs on SearXNG with no 402s.
- [ ] Optional, Grant: delete `OLLAMA_WEB_API_KEY` from the droplet `.env`. It is harmless now.
